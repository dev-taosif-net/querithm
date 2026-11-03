// Sidecar lifecycle (§4): spawn, health check, restart once, kill-on-exit. One sidecar serves every tab.
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::{sync_channel, Receiver, SyncSender, TrySendError};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde_json::{json, Value};
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::io::AsyncWriteExt;
use tokio::sync::oneshot;

use crate::ipc::Relay;

type Reply = Result<Value, Value>;

/// Calls waiting for their reply, by JSON-RPC id; shared by the caller, the stdin writer and the stdout reader.
type Pending = Mutex<HashMap<u64, oneshot::Sender<Reply>>>;

fn down(message: &str) -> Value {
    json!({ "code": -32003, "message": message, "data": { "code": "sidecarDown" } })
}

/// M8: a request not yet written to the engine's stdin when this has passed is failed instead of written: the engine has
/// stopped reading its requests (a full pipe). A write in progress for longer makes new calls fail at once. R1-1: both
/// apply only once the engine has answered a first request; before, it may still be starting (a first start extracts
/// native libraries, an antivirus scans them: well over 15 s), and requests wait for it — the health check still ends
/// an engine that never comes up.
const WRITE_DEADLINE: Duration = Duration::from_secs(10);
/// Requests waiting for the writer; past it a call fails at once (the writer only falls behind when the engine stopped reading).
const WRITE_QUEUE: usize = 1024;

/// One line for the engine's stdin: a request (`id`) or a notification. No deadline: queued before the engine answered
/// anything (R1-1).
struct Outgoing {
    id: Option<u64>,
    line: Vec<u8>,
    deadline: Option<Instant>,
}

/// M8: the only writer of the engine's stdin, on a thread of its own. A blocking `write_all` there can no longer hold a
/// tokio worker or a lock other calls need: callers hand it a line through a bounded queue (`try_send`, never waiting),
/// and the health ping, which goes through the same queue, misses when the writer is stuck, so the supervisor still
/// sees an engine that stopped reading and restarts it.
struct Writer {
    tx: SyncSender<Outgoing>,
    /// When the write in progress began (ms since `epoch`, at least 1); 0 while idle.
    busy_since: Arc<AtomicU64>,
    /// When the engine first answered (ms since `epoch`, at least 1); 0 until then: no deadline, no stall refusal (R1-1).
    armed_at: AtomicU64,
    epoch: Instant,
    deadline: Duration,
}

impl Writer {
    fn start(out: Box<dyn Write + Send>, pending: Arc<Pending>, deadline: Duration, capacity: usize) -> std::io::Result<Writer> {
        let (tx, rx) = sync_channel(capacity);
        let busy_since = Arc::new(AtomicU64::new(0));
        let epoch = Instant::now();
        let busy = busy_since.clone();
        std::thread::Builder::new().name("sidecar-stdin".into()).spawn(move || write_loop(out, rx, &pending, &busy, epoch))?;
        Ok(Writer { tx, busy_since, armed_at: AtomicU64::new(0), epoch, deadline })
    }

    /// The engine answered a request: it is up and reading, so from now on a line it does not take in time is failed.
    fn arm(&self) {
        let now = (self.epoch.elapsed().as_millis() as u64).max(1);
        let _ = self.armed_at.compare_exchange(0, now, Ordering::SeqCst, Ordering::SeqCst);
    }

    /// A write has been in progress for longer than the deadline (counted from when the engine first answered, at the
    /// earliest): the engine is not reading its stdin. Never while it has not answered yet.
    fn stalled(&self) -> Option<Duration> {
        let armed = self.armed_at.load(Ordering::SeqCst);
        let since = self.busy_since.load(Ordering::SeqCst);
        if since == 0 || armed == 0 {
            return None;
        }
        let waited = self.epoch.elapsed().saturating_sub(Duration::from_millis(since.max(armed)));
        (waited > self.deadline).then_some(waited)
    }

    /// Queues one line; never waits. The error says why it was not queued.
    fn send(&self, id: Option<u64>, line: Vec<u8>) -> Result<(), String> {
        if let Some(waited) = self.stalled() {
            return Err(format!("The Querithm engine has not read a request for {} s; it is restarted if it does not recover.", waited.as_secs()));
        }
        let deadline = (self.armed_at.load(Ordering::SeqCst) != 0).then(|| Instant::now() + self.deadline);
        self.tx.try_send(Outgoing { id, line, deadline }).map_err(|e| match e {
            TrySendError::Full(_) => "The Querithm engine is not reading its requests (too many are waiting).".to_string(),
            TrySendError::Disconnected(_) => "The Querithm engine's input is closed.".to_string(),
        })
    }
}

fn write_loop(mut out: Box<dyn Write + Send>, rx: Receiver<Outgoing>, pending: &Pending, busy: &AtomicU64, epoch: Instant) {
    let fail = |id: Option<u64>, message: &str| {
        if let Some(tx) = id.and_then(|id| pending.lock().unwrap().remove(&id)) {
            let _ = tx.send(Err(down(message)));
        }
    };
    let mut broken: Option<String> = None;
    // Ends when the process and every caller holding it are gone (the last sender drops).
    for item in rx {
        if let Some(reason) = &broken {
            fail(item.id, reason);
            continue;
        }
        if item.deadline.is_some_and(|d| Instant::now() > d) {
            fail(item.id, "The request was not sent: the Querithm engine stopped reading its requests.");
            continue;
        }
        busy.store((epoch.elapsed().as_millis() as u64).max(1), Ordering::SeqCst);
        let written = out.write_all(&item.line).and_then(|_| out.flush());
        busy.store(0, Ordering::SeqCst);
        if let Err(e) = written {
            // A killed engine closes the pipe: this and every later request fail with the reason.
            let reason = format!("engine pipe closed: {e}");
            fail(item.id, &reason);
            broken = Some(reason);
        }
    }
}

pub struct Process {
    /// None only for the tests' processes without a child.
    child: Mutex<Option<Child>>,
    writer: Writer,
    pending: Arc<Pending>,
    next_id: AtomicU64,
    alive: AtomicBool,
    pub pid: u32,
    pub endpoint: String,
    #[cfg(windows)]
    _job: isize,
}

impl Process {
    fn new(child: Option<Child>, stdin: Box<dyn Write + Send>, pid: u32, endpoint: String) -> std::io::Result<Process> {
        Self::with_writer(child, stdin, pid, endpoint, WRITE_DEADLINE, WRITE_QUEUE)
    }

    fn with_writer(child: Option<Child>, stdin: Box<dyn Write + Send>, pid: u32, endpoint: String, deadline: Duration, capacity: usize) -> std::io::Result<Process> {
        let pending: Arc<Pending> = Arc::new(Mutex::new(HashMap::new()));
        Ok(Process {
            writer: Writer::start(stdin, pending.clone(), deadline, capacity)?,
            child: Mutex::new(child),
            pending,
            next_id: AtomicU64::new(1),
            alive: AtomicBool::new(true),
            pid,
            endpoint,
            #[cfg(windows)]
            _job: 0,
        })
    }

    fn fail_pending(&self, message: &str) {
        for (_, tx) in self.pending.lock().unwrap().drain() {
            let _ = tx.send(Err(down(message)));
        }
    }

    fn exited(&self) -> bool {
        self.child.lock().unwrap().as_mut().map_or(false, |c| c.try_wait().ok().flatten().is_some())
    }

    fn kill(&self) {
        if let Some(c) = self.child.lock().unwrap().as_mut() {
            let _ = c.kill();
        }
    }

    /// One line from the engine's stdout: a reply resolves its call; a notification is returned for the webview.
    fn on_line(&self, line: &str) -> Option<Value> {
        let msg = serde_json::from_str::<Value>(line).ok()?;
        if let Some(id) = msg.get("id").and_then(Value::as_u64) {
            // R1-1: a reply means the engine is up and reading its requests.
            self.writer.arm();
            if let Some(tx) = self.pending.lock().unwrap().remove(&id) {
                let reply = match msg.get("error") {
                    Some(err) => Err(err.clone()),
                    None => Ok(msg.get("result").cloned().unwrap_or(Value::Null)),
                };
                let _ = tx.send(reply);
            }
            return None;
        }
        msg.get("method").is_some().then(|| json!({ "method": msg["method"], "params": msg.get("params").cloned().unwrap_or(Value::Null) }))
    }

    /// One JSON-RPC request; the reply comes back on the stdout reader thread. Queuing the line is the only work done
    /// here (one channel send), so a call never waits on the pipe.
    async fn call(&self, method: &str, params: Value) -> Reply {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let mut msg = json!({ "jsonrpc": "2.0", "id": id, "method": method });
        if !params.is_null() {
            msg["params"] = params;
        }
        let mut line = serde_json::to_vec(&msg).map_err(|e| json!({ "code": -32700, "message": e.to_string() }))?;
        line.push(b'\n');
        let (tx, rx) = oneshot::channel();
        self.pending.lock().unwrap().insert(id, tx);
        if let Err(e) = self.writer.send(Some(id), line) {
            self.pending.lock().unwrap().remove(&id);
            return Err(down(&e));
        }
        rx.await.unwrap_or_else(|_| Err(down("The Querithm engine stopped.")))
    }
}

/// §4 step 3: what one health check found.
#[derive(Debug, PartialEq, Eq)]
enum Probe {
    Healthy,
    Miss,
    Exited,
}

/// Counts consecutive misses: the third makes the engine dead (an exit does at once).
#[derive(Default)]
struct Health {
    misses: u32,
}

impl Health {
    const DEAD_AFTER: u32 = 3;

    fn record(&mut self, probe: &Probe) -> bool {
        self.misses = if *probe == Probe::Healthy { 0 } else { self.misses + 1 };
        let dead = *probe == Probe::Exited || self.misses >= Self::DEAD_AFTER;
        if dead {
            self.misses = 0;
        }
        dead
    }
}

/// One health check: system.ping must come back within `timeout`. The ping goes through the same stdin writer as every
/// call, so a writer stuck on a pipe the engine does not drain makes it miss (M8).
async fn probe(p: &Process, timeout: Duration) -> Probe {
    if p.exited() {
        return Probe::Exited;
    }
    match tokio::time::timeout(timeout, p.call("system.ping", Value::Null)).await {
        Ok(Ok(_)) => Probe::Healthy,
        _ => Probe::Miss,
    }
}

#[derive(Default)]
pub struct Supervisor {
    current: Mutex<Option<Arc<Process>>>,
    generation: AtomicU32,
    restarts: AtomicU32,
    last_restart: Mutex<Option<Instant>>,
    state: Mutex<String>,
    /// Why the engine is not running (M18): shown by the webview, also to a window that loads after the event went out.
    detail: Mutex<Option<String>>,
    /// Unix (L3): this shell's private folder for the data-plane sockets, made on the first spawn, removed at exit.
    #[cfg(unix)]
    socket_dir: Mutex<Option<socket_dir::PrivateDir>>,
    /// R1-2: the shared lock on this build's native-library marker, held for the shell's life so no other build's sweep
    /// removes the folder its engine loads libraries from.
    native_lock: Mutex<Option<std::fs::File>>,
}

impl Supervisor {
    fn process(&self) -> Option<Arc<Process>> {
        self.current.lock().unwrap().clone()
    }

    fn set_state(&self, app: &AppHandle, state: &str, detail: Option<String>) {
        let payload = self.record(state, detail);
        let _ = app.emit("sidecar://status", payload);
    }

    /// Keeps the state and its reason (a failure also goes to the shell log) and returns the status event's payload.
    fn record(&self, state: &str, detail: Option<String>) -> Value {
        if state == "failed" || state == "dead" {
            crate::shell_log::write("error", &format!("engine {state}: {}", detail.as_deref().unwrap_or("no details")));
        }
        *self.state.lock().unwrap() = state.to_string();
        *self.detail.lock().unwrap() = detail.clone();
        json!({ "state": state, "detail": detail, "restarts": self.restarts.load(Ordering::Relaxed) })
    }

    /// What `sidecar_status` answers: the state, why, and the running engine's pid.
    fn status(&self) -> Value {
        let p = self.process();
        json!({
            "state": *self.state.lock().unwrap(),
            "detail": *self.detail.lock().unwrap(),
            "pid": p.as_ref().map(|p| p.pid),
            "endpoint": p.as_ref().map(|p| p.endpoint.clone()),
            "restarts": self.restarts.load(Ordering::Relaxed),
        })
    }

    /// One JSON-RPC request to the running engine (see [`Process::call`]).
    pub async fn call(&self, method: &str, params: Value) -> Reply {
        let p = self.process().ok_or_else(|| down("The Querithm engine is not running."))?;
        p.call(method, params).await
    }

    /// §4 step 5 at exit (and before an update installs): system.shutdown, 2 s, then kill; the socket folder goes in any
    /// case (R1-6: also when no engine runs, a failed start or restart).
    pub fn shutdown(&self) {
        if let Some(p) = self.current.lock().unwrap().take() {
            Self::stop(&p);
        }
        // The engine is gone: its socket folder goes too (a crash leaves it to the OS's temp cleanup; nothing in it is secret).
        #[cfg(unix)]
        if let Some(dir) = self.socket_dir.lock().unwrap().take() {
            let _ = std::fs::remove_dir_all(&dir.path);
        }
    }

    fn stop(p: &Process) {
        p.alive.store(false, Ordering::SeqCst);
        // Queued like any request: an engine that stopped reading cannot hold the exit (it is killed after 2 s).
        let _ = p.writer.send(None, b"{\"jsonrpc\":\"2.0\",\"method\":\"system.shutdown\"}\n".to_vec());
        let deadline = Instant::now() + Duration::from_secs(2);
        if let Some(child) = p.child.lock().unwrap().as_mut() {
            let mut exited = false;
            while Instant::now() < deadline {
                if let Ok(Some(_)) = child.try_wait() {
                    exited = true;
                    break;
                }
                std::thread::sleep(Duration::from_millis(50));
            }
            if !exited {
                let _ = child.kill();
                let _ = child.wait();
            }
        }
        p.fail_pending("The Querithm engine stopped.");
    }
}

fn sidecar_path() -> PathBuf {
    // L10: only a debug shell runs another engine binary. A release shell hands that binary every secret it injects, so it
    // always runs the one bundled beside it (the bench and smoke harnesses point QUERITHM_SIDECAR at the dev bridge, not here).
    #[cfg(debug_assertions)]
    if let Ok(p) = std::env::var("QUERITHM_SIDECAR") {
        return PathBuf::from(p);
    }
    let exe = std::env::current_exe().expect("current exe");
    let dir = exe.parent().expect("exe dir");
    // tauri bundles externalBin next to the main executable without the target-triple suffix.
    dir.join(if cfg!(windows) { "querithm-sidecar.exe" } else { "querithm-sidecar" })
}

/// Who the data-plane server must be before the launch token is sent (L3): the spawned engine, run by this user.
#[derive(Clone, Copy)]
struct Peer {
    pid: u32,
    #[cfg(unix)]
    uid: u32,
}

/// The data plane's address. Windows: a pipe name with a random part (the server is checked by process id on connect).
/// Unix: a socket in this shell's private folder (socket_dir), never a guessable path in a shared folder.
#[cfg(windows)]
fn endpoint(_sup: &Supervisor, generation: u32) -> Result<(String, Option<u32>), String> {
    Ok((format!("querithm-{}-{generation}-{}", std::process::id(), random_hex(8)), None))
}

#[cfg(unix)]
fn endpoint(sup: &Supervisor, generation: u32) -> Result<(String, Option<u32>), String> {
    let mut slot = sup.socket_dir.lock().unwrap();
    if slot.as_ref().map_or(true, |d| !socket_dir::is_private(&d.path, d.uid)) {
        let xdg = if cfg!(target_os = "macos") { None } else { std::env::var_os("XDG_RUNTIME_DIR").map(PathBuf::from) };
        *slot = Some(socket_dir::choose(xdg, std::env::temp_dir()).map_err(|e| format!("could not make a private folder for the engine's socket: {e}"))?);
    }
    let dir = slot.as_ref().unwrap();
    Ok((socket_dir::socket_path(&dir.path, generation).to_string_lossy().into_owned(), Some(dir.uid)))
}

fn random_hex(bytes: usize) -> String {
    let mut buf = vec![0u8; bytes];
    getrandom::getrandom(&mut buf).expect("os randomness");
    buf.iter().map(|b| format!("{b:02x}")).collect()
}

fn random_token() -> String {
    random_hex(32)
}

/// L3 (§13 rule 4): the folder the data-plane socket is bound in. `$XDG_RUNTIME_DIR` only when it is this user's private
/// folder; otherwise the temp folder (`$TMPDIR`, else `/tmp`). Either way the socket goes into a fresh folder of its own,
/// `querithm-<random>`, made 0700 by this process, so no other user can create, replace or reach the path. A folder
/// that is not a real directory owned by the user with no group/other permission is never used.
#[cfg(unix)]
mod socket_dir {
    use std::fs;
    use std::io;
    use std::os::unix::fs::{DirBuilderExt, MetadataExt, PermissionsExt};
    use std::path::{Path, PathBuf};

    /// `sun_path` holds 104 (macOS) or 108 (Linux) bytes with its NUL; a longer base is skipped for the next one.
    pub const MAX_SOCKET_PATH: usize = 100;

    pub struct PrivateDir {
        pub path: PathBuf,
        /// This process's uid, learned from the folder it made (std has no getuid).
        pub uid: u32,
    }

    pub fn socket_path(dir: &Path, generation: u32) -> PathBuf {
        dir.join(format!("dp-{generation}.sock"))
    }

    /// A real directory (not a link), owned by `uid`, with no permission for group or others.
    pub fn is_private(path: &Path, uid: u32) -> bool {
        fs::symlink_metadata(path).map(|m| m.is_dir() && m.uid() == uid && m.mode() & 0o077 == 0).unwrap_or(false)
    }

    /// A folder others may write in only if it is sticky (they cannot rename or delete what this user made there), owned
    /// by this user or root.
    fn base_is_safe(base: &Path, uid: u32) -> bool {
        fs::metadata(base).map(|m| m.is_dir() && (m.uid() == uid || m.uid() == 0) && (m.mode() & 0o022 == 0 || m.mode() & 0o1000 != 0)).unwrap_or(false)
    }

    /// Makes `<base>/querithm-<random>` 0700. `mkdir` fails on an existing name (a link included), so the folder is
    /// always this process's own.
    pub fn create(base: &Path) -> io::Result<PrivateDir> {
        for _ in 0..8 {
            let path = base.join(format!("querithm-{}", super::random_hex(6)));
            match fs::DirBuilder::new().mode(0o700).create(&path) {
                Ok(()) => {
                    fs::set_permissions(&path, fs::Permissions::from_mode(0o700))?;
                    let uid = fs::symlink_metadata(&path)?.uid();
                    if !is_private(&path, uid) {
                        let _ = fs::remove_dir(&path);
                        return Err(io::Error::new(io::ErrorKind::PermissionDenied, "the new folder is not private"));
                    }
                    return Ok(PrivateDir { path, uid });
                }
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(e) => return Err(e),
            }
        }
        Err(io::Error::new(io::ErrorKind::AlreadyExists, "no free folder name"))
    }

    /// The private folder in the first usable base: `xdg` when it is private to this user, then `tmp`, then `/tmp`.
    pub fn choose(xdg: Option<PathBuf>, tmp: PathBuf) -> io::Result<PrivateDir> {
        let mut last = io::Error::new(io::ErrorKind::NotFound, "no usable folder");
        let candidates = xdg.into_iter().map(|p| (p, true)).chain([(tmp, false), (PathBuf::from("/tmp"), false)]);
        for (base, private) in candidates {
            let dir = match create(&base) {
                Ok(dir) => dir,
                Err(e) => {
                    last = e;
                    continue;
                }
            };
            let base_ok = if private { fs::canonicalize(&base).map(|b| is_private(&b, dir.uid)).unwrap_or(false) } else { base_is_safe(&base, dir.uid) };
            if base_ok && socket_path(&dir.path, u32::MAX).as_os_str().len() <= MAX_SOCKET_PATH {
                return Ok(dir);
            }
            let _ = fs::remove_dir(&dir.path);
            last = io::Error::new(io::ErrorKind::PermissionDenied, format!("{} is not a private or sticky folder of this user", base.display()));
        }
        Err(last)
    }
}

#[cfg(windows)]
fn assign_job(child: &Child) -> isize {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::System::JobObjects::*;
    // §4 step 4: the job kills the sidecar when the shell's last handle closes, even on a crash.
    unsafe {
        let job = CreateJobObjectW(std::ptr::null(), std::ptr::null());
        let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            &info as *const _ as *const core::ffi::c_void,
            std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        );
        AssignProcessToJobObject(job, child.as_raw_handle() as _);
        job as isize
    }
}

/// `<app local data dir>/native`: the root of the per-build extraction folders (native.rs), outside every webview scope.
fn native_root(app: &AppHandle) -> Result<PathBuf, String> {
    Ok(app.path().app_local_data_dir().map_err(|e| e.to_string())?.join("native"))
}

fn native_key(app: &AppHandle) -> String {
    crate::native::build_key(&app.package_info().version.to_string(), &sidecar_path())
}

/// The engine's command with piped stdio (and no console window on Windows), before its environment is set.
fn command(bin: &std::path::Path) -> Command {
    let mut cmd = Command::new(bin);
    cmd.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    cmd
}

/// Why the engine did not start, for the banner (M18): a missing binary is most often an antivirus quarantine.
fn start_error(bin: &std::path::Path, e: &std::io::Error) -> String {
    let hint = if e.kind() == std::io::ErrorKind::NotFound {
        " The engine file is missing; an antivirus program may have quarantined it. Restore it or reinstall Querithm."
    } else if e.kind() == std::io::ErrorKind::PermissionDenied {
        " The system refused to run it; an antivirus program or a security policy may block it."
    } else {
        ""
    };
    format!("could not start {}: {e}.{hint}", bin.display())
}

/// The engine's environment (§4 step 1): its launch token, socket, parent pid, data folder and native-library folder.
fn engine_env(cmd: &mut Command, token: &str, endpoint: &str, data_dir: &std::path::Path, native: &std::path::Path, release: bool) {
    // M21: the launch token never reaches a log line, the shell's included (kept apart from the passwords: R1-10).
    crate::shell_log::add_token(token);
    cmd.env("QUERITHM_TOKEN", token)
        .env("QUERITHM_SOCKET", endpoint)
        .env("QUERITHM_PARENT_PID", std::process::id().to_string())
        .env("QUERITHM_DATA_DIR", data_dir)
        // The single-file sidecar extracts its native libraries (SQLite, DuckDB, SNI) on first run: under the app's local
        // data folder, one folder per build, where no webview fs scope reaches (H7, §13 rule 6) — not .NET's default
        // folder in the user's home (Phase D3).
        .env("DOTNET_BUNDLE_EXTRACT_BASE_DIR", native);
    // M21: a release sidecar writes its log lines to its own file only; what reaches stderr then is what it printed outside
    // its logger (a runtime crash, a native library), which the shell keeps in its log.
    if release {
        cmd.env("QUERITHM_LOG_STDERR", "0");
    }
}

/// Spawns the sidecar. Must run on the main thread (see main.rs).
fn spawn(app: &AppHandle) -> Result<Arc<Process>, String> {
    let sup = app.state::<Supervisor>();
    let generation = sup.generation.fetch_add(1, Ordering::SeqCst);
    let token = random_token();
    let (endpoint, _uid) = endpoint(&sup, generation)?;
    let data_dir = app.path().data_dir().map_err(|e| e.to_string())?.join("querithm");
    let (native, lock) = crate::native::prepare(&native_root(app)?, &native_key(app));
    if let Some(lock) = lock {
        // A restart takes the lock again before the earlier handle goes: the folder is never unlocked meanwhile.
        let _earlier = sup.native_lock.lock().unwrap().replace(lock);
    }
    let bin = sidecar_path();

    let mut cmd = command(&bin);
    engine_env(&mut cmd, &token, &endpoint, &data_dir, &native, !cfg!(debug_assertions));
    let mut child = cmd.spawn().map_err(|e| start_error(&bin, &e))?;
    #[cfg(windows)]
    let job = assign_job(&child);
    let stdin = child.stdin.take().ok_or("no stdin")?;
    let stdout = child.stdout.take().ok_or("no stdout")?;
    let stderr = child.stderr.take().ok_or("no stderr")?;
    let pid = child.id();
    let peer = Peer {
        pid,
        #[cfg(unix)]
        uid: _uid.ok_or("no socket folder owner")?,
    };

    let process = Process::new(Some(child), Box::new(stdin), pid, endpoint.clone()).map_err(|e| e.to_string())?;
    #[cfg(windows)]
    let process = Process { _job: job, ..process };
    let process = Arc::new(process);

    // Control plane reader: replies resolve pending calls; notifications become Tauri events.
    {
        let p = process.clone();
        let app = app.clone();
        std::thread::Builder::new()
            .name("sidecar-stdout".into())
            .spawn(move || {
                for line in BufReader::with_capacity(1 << 20, stdout).lines() {
                    let Ok(line) = line else { break };
                    if let Some(notification) = p.on_line(&line) {
                        let _ = app.emit("sidecar://notify", notification);
                    }
                }
                p.fail_pending("The Querithm engine stopped.");
            })
            .map_err(|e| e.to_string())?;
    }
    std::thread::Builder::new()
        .name("sidecar-stderr".into())
        .spawn(move || {
            // M21: kept in the shell log (scrubbed); a release build on Windows has no console to print it to.
            for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                crate::shell_log::write("sidecar", &line);
            }
        })
        .map_err(|e| e.to_string())?;

    // Data plane: connect, authenticate with the launch token, relay frames; reconnect while this sidecar lives. Without
    // a reader no run can show rows (the sidecar then fails runs with the reason and the webview shows a banner).
    {
        let app = app.clone();
        let p = process.clone();
        // R1-3: this engine's generation; after a restart cleared the relay, this reader's late frames go nowhere.
        let generation = app.state::<Relay>().generation();
        tauri::async_runtime::spawn(async move {
            loop {
                let stream = match retry_until(DATA_CONNECT_WINDOW, Duration::from_millis(50), || connect(&endpoint, &token, peer)).await {
                    Ok(stream) => stream,
                    Err(e) => {
                        crate::shell_log::write("error", &format!("data plane connection failed: {e}"));
                        break;
                    }
                };
                // A torn or corrupt frame ends the read with an error; dropping the stream closes the connection, and the
                // sidecar's next write finds it closed and waits for this reconnect (§5).
                if let Err(e) = crate::ipc::read_frames(&app.state::<Relay>(), generation, stream).await {
                    if e.kind() != std::io::ErrorKind::UnexpectedEof {
                        crate::shell_log::write("warn", &format!("data plane closed: {e}"));
                    }
                }
                if !p.alive.load(Ordering::SeqCst) {
                    break;
                }
                crate::shell_log::write("warn", "data plane closed while the engine runs; reconnecting");
            }
        });
    }
    Ok(process)
}

/// How long the shell keeps trying to reach the data plane: a first start that extracts native libraries, or a slow
/// disk, can take well over the 15 s this used to be (a Debug sidecar under the dev bridge took 18 s).
const DATA_CONNECT_WINDOW: Duration = Duration::from_secs(60);

/// Calls `attempt` every `interval` until it succeeds, or returns its last error once `window` has passed.
async fn retry_until<T, F, Fut>(window: Duration, interval: Duration, mut attempt: F) -> std::io::Result<T>
where
    F: FnMut() -> Fut,
    Fut: std::future::Future<Output = std::io::Result<T>>,
{
    let deadline = Instant::now() + window;
    loop {
        match attempt().await {
            Ok(v) => return Ok(v),
            Err(e) if Instant::now() >= deadline => return Err(e),
            Err(_) => tokio::time::sleep(interval).await,
        }
    }
}

/// The data-plane server is the engine this shell spawned (L3): run by this user (Unix) and with the child's process id.
/// A platform that cannot report the peer's pid (some BSDs) is checked by user only; the socket folder is private anyway.
fn verify_peer(peer_pid: Option<u32>, peer_uid: Option<u32>, expected: Peer) -> std::io::Result<()> {
    #[cfg(unix)]
    if peer_uid != Some(expected.uid) {
        return Err(std::io::Error::new(std::io::ErrorKind::PermissionDenied, format!("the results socket belongs to user {peer_uid:?}, not this user")));
    }
    #[cfg(not(unix))]
    let _ = peer_uid;
    match peer_pid {
        Some(pid) if pid != expected.pid => Err(std::io::Error::new(
            std::io::ErrorKind::PermissionDenied,
            format!("the results socket is served by process {pid}, not the engine ({})", expected.pid),
        )),
        _ => Ok(()),
    }
}

/// Connects, checks who serves the socket, and only then sends the launch token (§13 rule 4).
#[cfg(unix)]
async fn connect(endpoint: &str, token: &str, peer: Peer) -> std::io::Result<tokio::net::UnixStream> {
    let mut stream = tokio::net::UnixStream::connect(endpoint).await?;
    let cred = stream.peer_cred()?;
    verify_peer(cred.pid().and_then(|p| u32::try_from(p).ok()), Some(cred.uid()), peer)?;
    stream.write_all(format!("{token}\n").as_bytes()).await?;
    Ok(stream)
}

#[cfg(windows)]
async fn connect(endpoint: &str, token: &str, peer: Peer) -> std::io::Result<tokio::net::windows::named_pipe::NamedPipeClient> {
    use std::os::windows::io::AsRawHandle;
    let mut pipe = tokio::net::windows::named_pipe::ClientOptions::new().open(format!(r"\\.\pipe\{endpoint}"))?;
    let mut server = 0u32;
    // SAFETY: the handle is the open pipe client's own; the call only writes the server's process id into `server`.
    if unsafe { windows_sys::Win32::System::Pipes::GetNamedPipeServerProcessId(pipe.as_raw_handle() as _, &mut server) } == 0 {
        return Err(std::io::Error::last_os_error());
    }
    verify_peer(Some(server), None, peer)?;
    pipe.write_all(format!("{token}\n").as_bytes()).await?;
    Ok(pipe)
}

/// Starts the first sidecar and the health monitor. A spawn that fails leaves the app running with the state "failed" and
/// the reason (M18): the webview shows it with Restart, which spawns again through `sidecar_restart`.
pub fn start(app: &AppHandle) {
    let sup = app.state::<Supervisor>();
    match spawn(app) {
        Ok(p) => {
            *sup.current.lock().unwrap() = Some(p);
            sup.set_state(app, "running", None);
        }
        Err(e) => sup.set_state(app, "failed", Some(start_failure(&e))),
    }
    // Off the startup path: the folder older builds extracted into (inside the webview's write scope, H7) and the
    // extraction folders of other builds this shell made.
    let legacy = app.path().data_dir().map(|d| d.join("querithm").join("native"));
    let (root, key) = (native_root(app), native_key(app));
    let _ = std::thread::Builder::new().name("native-sweep".into()).spawn(move || {
        let now = std::time::SystemTime::now();
        if let (Ok(legacy), Ok(root)) = (&legacy, &root) {
            crate::native::remove_legacy(legacy, &crate::native::legacy_seen(root), now);
        }
        if let Ok(root) = root {
            crate::native::sweep(&root, &key, now);
        }
    });
    let app = app.clone();
    tauri::async_runtime::spawn(async move { monitor(app).await });
}

/// The banner text of an engine that did not start at launch.
fn start_failure(e: &str) -> String {
    format!("The Querithm engine did not start: {e}")
}

/// §4 step 3: system.ping every 5 s; three misses → dead → banner, restart once, webview replays sessions.
async fn monitor(app: AppHandle) {
    let mut health = Health::default();
    loop {
        tokio::time::sleep(Duration::from_secs(5)).await;
        let sup = app.state::<Supervisor>();
        let Some(p) = sup.process() else { continue };
        if !p.alive.load(Ordering::SeqCst) {
            continue;
        }
        let found = probe(&p, Duration::from_secs(3)).await;
        if health.record(&found) {
            let exited = found == Probe::Exited;
            p.alive.store(false, Ordering::SeqCst);
            // Killing the engine closes its stdin: a write stuck on it fails, and so does every request still queued.
            p.kill();
            p.fail_pending("The Querithm engine stopped.");
            app.state::<Relay>().clear();
            sup.set_state(&app, "dead", Some(if exited { "exited".into() } else { "unresponsive".into() }));
            let recent = sup.last_restart.lock().unwrap().map(|t| t.elapsed() < Duration::from_secs(60)).unwrap_or(false);
            if recent {
                sup.set_state(&app, "failed", Some("The engine stopped twice in a minute; restart it from the banner.".into()));
                *sup.current.lock().unwrap() = None;
            } else {
                restart(&app).await;
            }
        }
    }
}

async fn restart(app: &AppHandle) {
    let sup = app.state::<Supervisor>();
    *sup.last_restart.lock().unwrap() = Some(Instant::now());
    sup.restarts.fetch_add(1, Ordering::SeqCst);
    let (tx, rx) = oneshot::channel();
    let handle = app.clone();
    // Spawn on the main thread again so PDEATHSIG is tied to a thread that lives as long as the app.
    let _ = app.run_on_main_thread(move || {
        let _ = tx.send(spawn(&handle));
    });
    match rx.await {
        Ok(Ok(p)) => {
            *sup.current.lock().unwrap() = Some(p);
            sup.set_state(app, "restarted", None);
        }
        Ok(Err(e)) => {
            *sup.current.lock().unwrap() = None;
            sup.set_state(app, "failed", Some(format!("The engine could not be restarted: {e}")));
        }
        Err(_) => {
            *sup.current.lock().unwrap() = None;
            sup.set_state(app, "failed", Some("The engine could not be restarted.".into()));
        }
    }
}

#[tauri::command]
pub fn sidecar_status(sup: State<'_, Supervisor>) -> Value {
    sup.status()
}

#[tauri::command]
pub async fn sidecar_restart(app: AppHandle) -> Result<(), String> {
    let sup = app.state::<Supervisor>();
    if let Some(p) = sup.current.lock().unwrap().take() {
        p.alive.store(false, Ordering::SeqCst);
        p.kill();
        p.fail_pending("The Querithm engine was restarted.");
    }
    app.state::<Relay>().clear();
    *sup.last_restart.lock().unwrap() = None;
    restart(&app).await;
    Ok(())
}

#[tauri::command]
pub fn app_info(app: AppHandle) -> Value {
    let data = app.path().data_dir().ok().map(|d| d.join("querithm"));
    json!({
        "version": app.package_info().version.to_string(),
        "platform": std::env::consts::OS,
        "bench": std::env::var("QUERITHM_BENCH").map(|v| v == "1").unwrap_or(false),
        "dataDir": data.as_ref().map(|d| d.to_string_lossy().into_owned()),
        // M21: where the sidecar's and the shell's logs are (Settings → About shows it with Copy path).
        "logsDir": data.as_ref().map(|d| d.join("logs").to_string_lossy().into_owned()),
        // M23: whether this instance owns the saved tabs (a second window starts with a new tab).
        "workspaceOwner": app.try_state::<crate::instance::Workspace>().map(|w| w.owner).unwrap_or(true),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;

    #[tokio::test]
    async fn retry_until_keeps_trying_while_the_engine_is_not_listening_yet() {
        let calls = AtomicUsize::new(0);
        let got = retry_until(Duration::from_secs(5), Duration::from_millis(1), || {
            let n = calls.fetch_add(1, Ordering::SeqCst);
            async move { if n < 3 { Err(std::io::Error::from(std::io::ErrorKind::NotFound)) } else { Ok(n) } }
        })
        .await;
        assert_eq!(got.unwrap(), 3);
        assert_eq!(calls.load(Ordering::SeqCst), 4);
    }

    #[test]
    fn the_peer_must_be_the_spawned_engine() {
        let engine = Peer {
            pid: 4242,
            #[cfg(unix)]
            uid: 1000,
        };
        #[cfg(unix)]
        {
            assert!(verify_peer(Some(4242), Some(1000), engine).is_ok());
            assert!(verify_peer(None, Some(1000), engine).is_ok(), "no pid reported: the user check stands");
            assert_eq!(verify_peer(Some(4242), Some(1001), engine).unwrap_err().kind(), std::io::ErrorKind::PermissionDenied);
            assert_eq!(verify_peer(Some(4243), Some(1000), engine).unwrap_err().kind(), std::io::ErrorKind::PermissionDenied);
        }
        #[cfg(windows)]
        {
            assert!(verify_peer(Some(4242), None, engine).is_ok());
            assert_eq!(verify_peer(Some(4243), None, engine).unwrap_err().kind(), std::io::ErrorKind::PermissionDenied);
        }
    }

    #[cfg(unix)]
    mod socket_folder {
        use super::super::*;
        use std::os::unix::fs::{MetadataExt, PermissionsExt};
        use tokio::io::AsyncReadExt;

        /// A scratch folder of the test's own (0700), removed by the caller.
        fn scratch(mode: u32) -> PathBuf {
            let d = std::env::temp_dir().join(format!("qsock-test-{}", random_hex(4)));
            std::fs::create_dir(&d).unwrap();
            std::fs::set_permissions(&d, std::fs::Permissions::from_mode(mode)).unwrap();
            d
        }

        fn mode(p: &std::path::Path) -> u32 {
            std::fs::symlink_metadata(p).unwrap().mode() & 0o7777
        }

        #[test]
        fn a_new_socket_folder_is_the_users_alone_and_fresh_each_time() {
            let base = scratch(0o700);
            let a = socket_dir::create(&base).unwrap();
            let b = socket_dir::create(&base).unwrap();
            assert_ne!(a.path, b.path);
            assert_eq!(mode(&a.path), 0o700);
            assert_eq!(std::fs::metadata(&a.path).unwrap().uid(), a.uid);
            assert!(socket_dir::is_private(&a.path, a.uid));
            std::fs::remove_dir_all(&base).unwrap();
        }

        #[test]
        fn a_folder_with_group_or_other_permission_another_owner_or_a_link_is_not_private() {
            let base = scratch(0o700);
            let dir = socket_dir::create(&base).unwrap();
            assert!(!socket_dir::is_private(&dir.path, dir.uid + 1), "another user's folder");
            std::fs::set_permissions(&dir.path, std::fs::Permissions::from_mode(0o750)).unwrap();
            assert!(!socket_dir::is_private(&dir.path, dir.uid), "group can read");
            std::fs::set_permissions(&dir.path, std::fs::Permissions::from_mode(0o701)).unwrap();
            assert!(!socket_dir::is_private(&dir.path, dir.uid), "others can enter");
            std::fs::set_permissions(&dir.path, std::fs::Permissions::from_mode(0o700)).unwrap();
            let link = base.join("link");
            std::os::unix::fs::symlink(&dir.path, &link).unwrap();
            assert!(!socket_dir::is_private(&link, dir.uid), "a link is never used");
            std::fs::remove_dir_all(&base).unwrap();
        }

        #[test]
        fn a_shared_xdg_runtime_dir_is_passed_over_for_the_temp_folder() {
            let (xdg, tmp) = (scratch(0o755), scratch(0o700));
            let dir = socket_dir::choose(Some(xdg.clone()), tmp.clone()).unwrap();
            assert!(dir.path.starts_with(&tmp), "{}", dir.path.display());
            assert_eq!(std::fs::read_dir(&xdg).unwrap().count(), 0, "nothing is left in the refused folder");
            std::fs::remove_dir_all(&xdg).unwrap();
            std::fs::remove_dir_all(&tmp).unwrap();
        }

        #[test]
        fn a_private_xdg_runtime_dir_holds_the_folder() {
            let (xdg, tmp) = (scratch(0o700), scratch(0o700));
            let dir = socket_dir::choose(Some(xdg.clone()), tmp.clone()).unwrap();
            assert!(dir.path.starts_with(&xdg));
            std::fs::remove_dir_all(&xdg).unwrap();
            std::fs::remove_dir_all(&tmp).unwrap();
        }

        #[test]
        fn a_world_writable_folder_that_is_not_sticky_or_too_long_a_path_is_passed_over() {
            let open = scratch(0o777);
            let dir = socket_dir::choose(None, open.clone()).unwrap();
            assert!(!dir.path.starts_with(&open) && dir.path.starts_with("/tmp"), "{}", dir.path.display());
            std::fs::remove_dir_all(&dir.path).unwrap();

            let home = scratch(0o700);
            let long = home.join("x".repeat(80));
            std::fs::create_dir(&long).unwrap();
            std::fs::set_permissions(&long, std::fs::Permissions::from_mode(0o700)).unwrap();
            let dir = socket_dir::choose(None, long.clone()).unwrap();
            assert!(!dir.path.starts_with(&long));
            assert!(socket_dir::socket_path(&dir.path, u32::MAX).as_os_str().len() <= socket_dir::MAX_SOCKET_PATH);
            std::fs::remove_dir_all(&dir.path).unwrap();
            std::fs::remove_dir_all(&open).unwrap();
            std::fs::remove_dir_all(&home).unwrap();
        }

        #[test]
        fn shutdown_removes_the_socket_folder_also_with_no_engine_running() {
            // R1-6: a start or restart that failed leaves no current engine; the folder made for it still goes at exit.
            let base = scratch(0o700);
            let dir = socket_dir::create(&base).unwrap();
            let path = dir.path.clone();
            let sup = Supervisor::default();
            *sup.socket_dir.lock().unwrap() = Some(dir);
            sup.shutdown();
            assert!(!path.exists());
            std::fs::remove_dir_all(&base).unwrap();
        }

        #[tokio::test]
        async fn the_launch_token_goes_only_to_the_spawned_engine() {
            let base = scratch(0o700);
            let dir = socket_dir::create(&base).unwrap();
            let path = socket_dir::socket_path(&dir.path, 0);
            let listener = tokio::net::UnixListener::bind(&path).unwrap();
            let server = tokio::spawn(async move {
                let mut got = Vec::new();
                for _ in 0..2 {
                    let (mut s, _) = listener.accept().await.unwrap();
                    let mut buf = Vec::new();
                    let _ = tokio::time::timeout(Duration::from_secs(5), s.read_to_end(&mut buf)).await;
                    got.push(String::from_utf8(buf).unwrap());
                }
                got
            });
            let endpoint = path.to_string_lossy().into_owned();
            // This test process is the listener: as the engine it gets the token; under another pid it gets nothing.
            let me = Peer { pid: std::process::id(), uid: dir.uid };
            drop(connect(&endpoint, "tok-1", me).await.unwrap());
            let impostor = Peer { pid: std::process::id() + 1, uid: dir.uid };
            assert_eq!(connect(&endpoint, "tok-2", impostor).await.unwrap_err().kind(), std::io::ErrorKind::PermissionDenied);
            assert_eq!(server.await.unwrap(), vec!["tok-1\n".to_string(), String::new()]);
            std::fs::remove_dir_all(&base).unwrap();
        }
    }

    #[test]
    fn a_missing_engine_binary_fails_with_a_reason_instead_of_aborting() {
        let bin = std::env::temp_dir().join(format!("no-such-dir-{}", random_hex(4))).join("querithm-sidecar");
        let e = command(&bin).spawn().map(|_| ()).map_err(|e| start_error(&bin, &e)).unwrap_err();
        assert!(e.starts_with(&format!("could not start {}: ", bin.display())), "{e}");
        assert!(e.contains("antivirus"), "{e}");
        // The supervisor keeps it as the state the webview asks for once it has loaded (the event went out before).
        let sup = Supervisor::default();
        let event = sup.record("failed", Some(start_failure(&e)));
        assert_eq!(event["state"], "failed");
        let status = sup.status();
        assert_eq!(status["state"], "failed");
        assert_eq!(status["detail"].as_str().unwrap(), format!("The Querithm engine did not start: {e}"));
        assert!(status["pid"].is_null());
        // A later start clears the reason.
        sup.record("restarted", None);
        assert!(sup.status()["detail"].is_null());
    }

    #[test]
    fn a_release_engine_keeps_its_log_lines_out_of_stderr_and_its_token_out_of_the_log() {
        let env = |release: bool| {
            let mut cmd = Command::new("querithm-sidecar");
            engine_env(&mut cmd, "launch-token-of-this-test", "ep", std::path::Path::new("/d"), std::path::Path::new("/n"), release);
            cmd.get_envs().map(|(k, v)| (k.to_string_lossy().into_owned(), v.map(|v| v.to_string_lossy().into_owned()))).collect::<HashMap<_, _>>()
        };
        let release = env(true);
        assert_eq!(release.get("QUERITHM_LOG_STDERR"), Some(&Some("0".to_string())));
        assert_eq!(release.get("QUERITHM_TOKEN"), Some(&Some("launch-token-of-this-test".to_string())));
        // The native-library folder is passed (its variable's name is checked by WebviewBoundarySecurityTests, on one line).
        assert!(release.values().any(|v| v.as_deref() == Some("/n")));
        assert!(!env(false).contains_key("QUERITHM_LOG_STDERR"), "a debug engine prints to stderr");
        assert!(crate::shell_log::remembers("launch-token-of-this-test"), "the token is scrubbed from every log line");
    }

    // ---- M8: the stdin writer

    /// A pipe the engine does not drain: each write waits until the test lets go (as one does on a full 64 KB pipe), then
    /// succeeds (`drained`) or fails as a killed engine's pipe does. Lines that got through are kept.
    #[derive(Clone)]
    struct Stuck {
        gate: Arc<(Mutex<Option<bool>>, std::sync::Condvar)>,
        written: Arc<Mutex<Vec<Vec<u8>>>>,
    }

    impl Stuck {
        fn new() -> Stuck {
            Stuck { gate: Arc::new((Mutex::new(None), std::sync::Condvar::new())), written: Arc::default() }
        }
        fn release(&self, drained: bool) {
            let (lock, cv) = &*self.gate;
            *lock.lock().unwrap() = Some(drained);
            cv.notify_all();
        }
    }

    impl Write for Stuck {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            let (lock, cv) = &*self.gate;
            let mut state = lock.lock().unwrap();
            while state.is_none() {
                state = cv.wait(state).unwrap();
            }
            if *state != Some(true) {
                return Err(std::io::Error::from(std::io::ErrorKind::BrokenPipe));
            }
            self.written.lock().unwrap().push(buf.to_vec());
            Ok(buf.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    // One runtime thread: had a call or the ping blocked on the pipe, the test itself would hang.
    #[tokio::test(flavor = "current_thread")]
    async fn an_engine_that_stops_reading_stdin_misses_its_pings_and_is_declared_dead() {
        let pipe = Stuck::new();
        let p = Arc::new(Process::with_writer(None, Box::new(pipe.clone()), 1, String::new(), Duration::from_millis(300), 64).unwrap());
        // An engine that started (it answered a request: R1-1), then stopped reading.
        p.writer.arm();
        // A whole document (lang.*) that the engine never reads.
        let big = tokio::spawn({
            let p = p.clone();
            async move { p.call("lang.diagnostics", json!({ "text": "x".repeat(1 << 20) })).await }
        });
        tokio::task::yield_now().await;
        let mut health = Health::default();
        let mut dead = false;
        for _ in 0..Health::DEAD_AFTER {
            let started = Instant::now();
            let found = probe(&p, Duration::from_millis(200)).await;
            assert_eq!(found, Probe::Miss);
            assert!(started.elapsed() < Duration::from_secs(2), "the ping waited on the pipe: {:?}", started.elapsed());
            assert!(!dead);
            dead = health.record(&found);
        }
        assert!(dead, "three misses make the engine dead");
        // Past the deadline a new call fails at once instead of joining the queue.
        let started = Instant::now();
        let refused = p.call("meta.databases", json!({})).await.unwrap_err();
        assert!(started.elapsed() < Duration::from_millis(100));
        assert!(refused["message"].as_str().unwrap().contains("has not read a request"), "{refused}");
        // What the supervisor then does: kill the engine (its pipe closes) and fail every waiting call with the reason.
        p.fail_pending("The Querithm engine stopped.");
        pipe.release(false);
        let reply = big.await.unwrap().unwrap_err();
        assert_eq!(reply["data"]["code"], "sidecarDown");
        assert_eq!(reply["message"], "The Querithm engine stopped.");
    }

    /// The engine's stdin as a channel; a fake engine thread answers each request through `on_line`, as the reader does.
    struct Lines(std::sync::mpsc::Sender<Vec<u8>>);

    impl Write for Lines {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            self.0.send(buf.to_vec()).map_err(|_| std::io::Error::from(std::io::ErrorKind::BrokenPipe))?;
            Ok(buf.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    #[tokio::test(flavor = "current_thread")]
    async fn requests_flow_through_the_writer_and_a_reading_engine_stays_healthy() {
        let (tx, rx) = std::sync::mpsc::channel::<Vec<u8>>();
        let p = Arc::new(Process::new(None, Box::new(Lines(tx)), 1, String::new()).unwrap());
        let engine = p.clone();
        std::thread::spawn(move || {
            for line in rx {
                let req: Value = serde_json::from_slice(&line).unwrap();
                let reply = json!({ "jsonrpc": "2.0", "id": req["id"], "result": { "method": req["method"], "n": req["params"]["n"] } });
                assert!(engine.on_line(&reply.to_string()).is_none(), "a reply is not a notification");
            }
        });
        let calls: Vec<_> = (0..200).map(|n| {
            let p = p.clone();
            tokio::spawn(async move { p.call("lang.complete", json!({ "n": n })).await })
        }).collect();
        for (n, call) in calls.into_iter().enumerate() {
            let r = call.await.unwrap().unwrap();
            assert_eq!((r["method"].as_str(), r["n"].as_u64()), (Some("lang.complete"), Some(n as u64)));
        }
        let mut health = Health::default();
        let found = probe(&p, Duration::from_secs(3)).await;
        assert_eq!(found, Probe::Healthy);
        assert!(!health.record(&found));
        let n = p.on_line(r#"{"jsonrpc":"2.0","method":"query.messages","params":{"executionId":"e1"}}"#).unwrap();
        assert_eq!((n["method"].as_str(), n["params"]["executionId"].as_str()), (Some("query.messages"), Some("e1")));
        assert!(p.on_line("not json").is_none());
    }

    #[test]
    fn a_request_still_queued_at_its_deadline_is_failed_unsent_and_a_full_queue_refuses_at_once() {
        let pipe = Stuck::new();
        let pending: Arc<Pending> = Arc::new(Mutex::new(HashMap::new()));
        let w = Writer::start(Box::new(pipe.clone()), pending.clone(), Duration::from_millis(100), 2).unwrap();
        w.arm(); // the engine had answered (R1-1)
        let mut replies = Vec::new();
        for id in 1..=3u64 {
            let (tx, rx) = oneshot::channel();
            pending.lock().unwrap().insert(id, tx);
            replies.push(rx);
        }
        w.send(Some(1), b"one\n".to_vec()).unwrap();
        while w.busy_since.load(Ordering::SeqCst) == 0 {
            std::thread::sleep(Duration::from_millis(1));
        }
        w.send(Some(2), b"two\n".to_vec()).unwrap();
        w.send(Some(3), b"three\n".to_vec()).unwrap();
        assert!(w.send(Some(4), b"four\n".to_vec()).unwrap_err().contains("too many are waiting"));
        std::thread::sleep(Duration::from_millis(150));
        assert!(w.stalled().is_some());
        pipe.release(true);
        // The first was being written and goes through; the two that waited past their deadline are never written.
        let _first = replies.remove(0);
        for rx in replies {
            let e = rx.blocking_recv().unwrap().unwrap_err();
            assert!(e["message"].as_str().unwrap().contains("was not sent"), "{e}");
            assert_eq!(e["data"]["code"], "sidecarDown");
        }
        assert_eq!(*pipe.written.lock().unwrap(), vec![b"one\n".to_vec()]);
        while w.busy_since.load(Ordering::SeqCst) != 0 {
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(w.stalled().is_none(), "a writer that caught up takes calls again");
    }

    #[test]
    fn an_engine_still_starting_gets_its_requests_late_but_never_fails_them_unsent() {
        // R1-1: a first start can take well over the deadline (native extraction, an antivirus). Before it answers anything,
        // a write stuck on its pipe neither fails the requests behind it nor refuses new ones.
        let pipe = Stuck::new();
        let p = Process::with_writer(None, Box::new(pipe.clone()), 1, String::new(), Duration::from_millis(100), 64).unwrap();
        let mut replies = Vec::new();
        for id in 1..=3u64 {
            let (tx, rx) = oneshot::channel();
            p.pending.lock().unwrap().insert(id, tx);
            p.writer.send(Some(id), format!("{id}\n").into_bytes()).unwrap();
            replies.push(rx);
        }
        while p.writer.busy_since.load(Ordering::SeqCst) == 0 {
            std::thread::sleep(Duration::from_millis(1));
        }
        std::thread::sleep(Duration::from_millis(300)); // three deadlines: still starting
        assert!(p.writer.stalled().is_none());
        p.writer.send(None, b"later\n".to_vec()).expect("a call is not refused while the engine starts");
        pipe.release(true);
        let started = Instant::now();
        while pipe.written.lock().unwrap().len() < 4 && started.elapsed() < Duration::from_secs(5) {
            std::thread::sleep(Duration::from_millis(2));
        }
        assert_eq!(*pipe.written.lock().unwrap(), vec![b"1\n".to_vec(), b"2\n".to_vec(), b"3\n".to_vec(), b"later\n".to_vec()]);
        for mut rx in replies {
            assert!(rx.try_recv().is_err(), "no request was failed unsent");
        }
        // Its first answer arms the deadline from then on.
        assert_eq!(p.writer.armed_at.load(Ordering::SeqCst), 0);
        assert!(p.on_line(r#"{"jsonrpc":"2.0","id":1,"result":null}"#).is_none());
        assert_ne!(p.writer.armed_at.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn a_closed_pipe_fails_the_request_being_written_and_every_later_one() {
        let pipe = Stuck::new();
        pipe.release(false);
        let pending: Arc<Pending> = Arc::new(Mutex::new(HashMap::new()));
        let w = Writer::start(Box::new(pipe), pending.clone(), Duration::from_secs(5), 8).unwrap();
        let mut replies = Vec::new();
        for id in 1..=2u64 {
            let (tx, rx) = oneshot::channel();
            pending.lock().unwrap().insert(id, tx);
            w.send(Some(id), b"x\n".to_vec()).unwrap();
            replies.push(rx);
        }
        for rx in replies {
            let e = rx.blocking_recv().unwrap().unwrap_err();
            assert!(e["message"].as_str().unwrap().starts_with("engine pipe closed"), "{e}");
        }
    }

    #[tokio::test]
    async fn retry_until_gives_up_with_the_last_error_once_the_window_has_passed() {
        let started = Instant::now();
        let got: std::io::Result<()> =
            retry_until(Duration::from_millis(100), Duration::from_millis(10), || async { Err(std::io::Error::from(std::io::ErrorKind::NotFound)) }).await;
        assert_eq!(got.unwrap_err().kind(), std::io::ErrorKind::NotFound);
        let took = started.elapsed();
        assert!(took >= Duration::from_millis(100) && took < Duration::from_secs(2), "{took:?}");
    }
}
