// IPC relay (§5). Control plane: JSON-RPC lines on the sidecar's stdio, forwarded verbatim.
// Data plane: Arrow frames from the local socket, buffered per execution and handed to the webview as raw bytes.
use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::Mutex;
use std::time::Duration;

use serde_json::{json, Value};
use tauri::ipc::Response;
use tauri::State;
use tokio::io::{AsyncRead, AsyncReadExt};
use tokio::sync::Notify;
use tokio::time::Instant;

use crate::sidecar::Supervisor;

/// Rust buffers at most 8 batches per execution, then pauses socket reads (backpressure, §5).
const MAX_BUFFERED_BATCHES: usize = 8;
/// The kinds the sidecar sends (`FrameKind`): schema, batch, end of result set, end of execution. R1-4: any other byte
/// there means the stream lost its framing; it is refused before the frame's payload is allocated.
const KIND_SCHEMA: u8 = 0x01;
const KIND_BATCH: u8 = 0x02;
const KIND_END_OF_EXECUTION: u8 = 0x04;
/// Made by the shell, never sent by the sidecar: results_next answers it for a run whose frames were dropped (its
/// reader stopped asking, or discarded them), so a poll loop still running for it ends instead of waiting forever.
const KIND_GONE: u8 = 0xFE;
/// What the length field counts besides the payload: kind, execution id, result index.
const FRAME_HEADER_LEN: usize = 1 + 16 + 2;
/// The largest length field accepted (`FrameLimits.MaxLength` in the sidecar, the same value). Batches flush at 10,000
/// rows or 4 MB, so one goes past 4 MB only by its last row, whose cells are capped (text 1 M characters, about 3 MB of
/// UTF-8 at most; binary 64 KB); schema, end and import-preview frames are far smaller, and a data-compare batch (flushed
/// at 1,000 diff rows or 4 MB, text not capped) comes near it only for one row of very long values. The sidecar fails a
/// run whose frame would be larger before writing it, so a length above this means the stream lost its framing: it is
/// never allocated, the connection is closed and the shell reconnects. scripts/dev-bridge-relay.mjs mirrors these limits.
const MAX_FRAME_LEN: usize = 256 * 1024 * 1024;
/// A full queue whose consumer has not asked for a frame for this long is dropped (until its end of execution), so one
/// run nobody reads never holds the reader every run shares (H3). results_next long-polls for at most 20 s and a
/// consumer that is reading calls it again at once (a full queue answers at once), so 30 s of silence on a full queue
/// means the consumer is gone, not slow.
const STALE_CONSUMER: Duration = Duration::from_secs(30);
/// How many ended (or dropped) runs are remembered, so a late results_discard or results_next for one leaves no entry.
const REMEMBERED_ENDS: usize = 1024;
/// How long one results_next call waits for a frame before answering "nothing yet".
const POLL_WAIT: Duration = Duration::from_secs(20);
// A consumer that is reading asks again at least every POLL_WAIT, so it is never taken for gone (R1-3's sweep).
const _: () = assert!(POLL_WAIT.as_secs() < STALE_CONSUMER.as_secs());

struct ExecQueue {
    frames: VecDeque<Vec<u8>>,
    batches: usize,
    discarded: bool,
    /// The end-of-execution frame is buffered: nothing more arrives for this run.
    ended: bool,
    /// When the consumer last asked for a frame (or when the queue was made).
    last_poll: Instant,
}

impl ExecQueue {
    fn new() -> Self {
        ExecQueue { frames: VecDeque::new(), batches: 0, discarded: false, ended: false, last_poll: Instant::now() }
    }

    fn drop_frames(&mut self) {
        self.discarded = true;
        self.frames.clear();
        self.batches = 0;
    }
}

#[derive(Default)]
struct Queues {
    by_id: HashMap<String, ExecQueue>,
    /// Runs whose end went through (consumed, or dropped with their queue), oldest first.
    ended: VecDeque<String>,
    ended_set: HashSet<String>,
    /// R1-3: bumped by `clear` (the engine went). A socket reader of an earlier engine pushes into nothing.
    generation: u64,
    /// When the next sweep for entries nobody polls is due.
    next_sweep: Option<Instant>,
}

impl Queues {
    fn remember_end(&mut self, id: String) {
        if self.ended_set.insert(id.clone()) {
            self.ended.push_back(id);
            if self.ended.len() > REMEMBERED_ENDS {
                if let Some(old) = self.ended.pop_front() {
                    self.ended_set.remove(&old);
                }
            }
        }
    }
}

pub struct Relay {
    queues: Mutex<Queues>,
    /// Wakes consumers waiting in results_next.
    arrived: Notify,
    /// Wakes the socket reader paused on a full queue.
    space: Notify,
    /// STALE_CONSUMER; shorter in tests.
    stale_after: Duration,
}

impl Default for Relay {
    fn default() -> Self {
        Relay::with_stale_after(STALE_CONSUMER)
    }
}

/// What one attempt to queue a frame came to.
enum Push {
    /// Queued (true: a consumer may be waiting for it) or dropped (false).
    Done(bool),
    /// The queue is full and its consumer is reading: try again once there is space, or after this long.
    Wait(Duration),
}

/// Writes lines gathered under the relay's lock once it is released (R1-5: a slow disk never holds the lock).
fn log_all(lines: Vec<String>) {
    for line in lines {
        crate::shell_log::write("warn", &line);
    }
}

impl Relay {
    fn with_stale_after(stale_after: Duration) -> Self {
        Relay { queues: Mutex::new(Queues::default()), arrived: Notify::new(), space: Notify::new(), stale_after }
    }

    /// The current engine's generation: a socket reader passes it with every frame (see `clear`).
    pub fn generation(&self) -> u64 {
        self.queues.lock().unwrap().generation
    }

    /// R1-3: entries nobody asked for a frame for `stale_after` go — a vanished consumer whose queue never filled, an
    /// end that was never read, a dropped run whose end never came (its engine was killed). Each is remembered as ended,
    /// so its later frames go nowhere and results_next answers "gone". At most every quarter of the bound; the lines
    /// to log are returned through `logs`.
    fn sweep(&self, queues: &mut Queues, logs: &mut Vec<String>) {
        let now = Instant::now();
        if queues.next_sweep.is_some_and(|due| now < due) {
            return;
        }
        queues.next_sweep = Some(now + self.stale_after / 4);
        let stale: Vec<String> = queues.by_id.iter().filter(|(_, q)| q.last_poll.elapsed() >= self.stale_after).map(|(id, _)| id.clone()).collect();
        for id in stale {
            if let Some(q) = queues.by_id.remove(&id) {
                if !q.discarded {
                    logs.push(format!("results of {id} dropped: nothing asked for them for {} s", q.last_poll.elapsed().as_secs()));
                }
                queues.remember_end(id);
            }
        }
    }

    /// Called by the socket reader for every frame. Awaits while this execution's queue is full and its consumer is
    /// reading; drops the queue once its consumer has been silent for `stale_after`. A frame of an earlier engine
    /// (`generation` older than the relay's) is dropped.
    async fn push(&self, generation: u64, exec_id: String, kind: u8, frame: Vec<u8>) {
        let mut frame = Some(frame);
        loop {
            let notified = self.space.notified();
            let mut logs = Vec::new();
            let step = self.try_push(generation, &exec_id, kind, &mut frame, &mut logs);
            log_all(logs);
            match step {
                Push::Done(arrived) => {
                    if arrived {
                        self.arrived.notify_waiters();
                    }
                    return;
                }
                // Woken by results_next taking a frame or results_discard; otherwise check the consumer again once it could be stale.
                Push::Wait(wait) => {
                    let _ = tokio::time::timeout(wait, notified).await;
                }
            }
        }
    }

    fn try_push(&self, generation: u64, exec_id: &str, kind: u8, frame: &mut Option<Vec<u8>>, logs: &mut Vec<String>) -> Push {
        let mut queues = self.queues.lock().unwrap();
        if queues.generation != generation {
            return Push::Done(false); // R1-3: the dying reader of an engine that went drains into nothing
        }
        self.sweep(&mut queues, logs);
        if queues.ended_set.contains(exec_id) {
            return Push::Done(false); // a run that already ended sends nothing more; never make it a new entry
        }
        let q = queues.by_id.entry(exec_id.to_owned()).or_insert_with(ExecQueue::new);
        if q.discarded {
            if kind == KIND_END_OF_EXECUTION {
                queues.by_id.remove(exec_id);
                queues.remember_end(exec_id.to_owned());
            }
            return Push::Done(false);
        }
        if kind != KIND_BATCH || q.batches < MAX_BUFFERED_BATCHES {
            if kind == KIND_BATCH {
                q.batches += 1;
            }
            if kind == KIND_END_OF_EXECUTION {
                q.ended = true;
            }
            q.frames.extend(frame.take());
            return Push::Done(true);
        }
        let silent = q.last_poll.elapsed();
        if silent >= self.stale_after {
            logs.push(format!("results of {exec_id} dropped: nothing asked for them for {} s", silent.as_secs()));
            q.drop_frames();
            return Push::Done(true);
        }
        Push::Wait(self.stale_after - silent)
    }

    /// The next frame of an execution, waiting up to `wait`. Empty = nothing yet; `[KIND_GONE, 0, 0]` = its frames were
    /// dropped (or it already ended), so there is nothing to wait for.
    async fn next(&self, exec_id: &str, wait: Duration) -> Vec<u8> {
        let deadline = Instant::now() + wait;
        loop {
            let notified = self.arrived.notified();
            let mut logs = Vec::new();
            let got = self.try_next(exec_id, &mut logs);
            log_all(logs);
            if let Some((frame, freed)) = got {
                if freed {
                    self.space.notify_waiters();
                }
                return frame;
            }
            if tokio::time::timeout_at(deadline, notified).await.is_err() {
                return Vec::new();
            }
        }
    }

    /// A frame (and whether a full queue got space), "gone", or None: nothing yet.
    fn try_next(&self, exec_id: &str, logs: &mut Vec<String>) -> Option<(Vec<u8>, bool)> {
        let mut queues = self.queues.lock().unwrap();
        self.sweep(&mut queues, logs);
        let ended = queues.ended_set.contains(exec_id);
        match queues.by_id.get_mut(exec_id) {
            Some(q) if q.discarded => Some((vec![KIND_GONE, 0, 0], false)),
            Some(q) => {
                q.last_poll = Instant::now();
                let frame = q.frames.pop_front()?;
                if frame[0] == KIND_BATCH {
                    q.batches -= 1;
                }
                if frame[0] == KIND_END_OF_EXECUTION {
                    queues.by_id.remove(exec_id);
                    queues.remember_end(exec_id.to_owned());
                }
                Some((frame, true))
            }
            None if ended => Some((vec![KIND_GONE, 0, 0], false)),
            None => None,
        }
    }

    /// Drops buffered and future frames of an execution. A run whose end already arrived (buffered or consumed) leaves
    /// no entry behind; any other becomes a discarded entry that its end-of-execution frame removes.
    fn discard(&self, exec_id: String) {
        let mut queues = self.queues.lock().unwrap();
        if !queues.ended_set.contains(&exec_id) {
            let q = queues.by_id.entry(exec_id.clone()).or_insert_with(ExecQueue::new);
            if q.ended {
                queues.by_id.remove(&exec_id);
                queues.remember_end(exec_id);
            } else {
                q.drop_frames();
            }
        }
        drop(queues);
        self.space.notify_waiters();
        self.arrived.notify_waiters();
    }

    /// The engine went (killed, restarted): every queue goes, and the next generation begins, so the old engine's
    /// reader — still draining its socket until it sees the end — makes no entry again (R1-3).
    pub fn clear(&self) {
        let mut queues = self.queues.lock().unwrap();
        *queues = Queues { generation: queues.generation + 1, ..Queues::default() };
        drop(queues);
        self.arrived.notify_waiters();
        self.space.notify_waiters();
    }
}

/// ULID bytes → 26-char Crockford base32, matching the sidecar's executionId strings.
fn ulid_string(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 32] = b"0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    let mut v: u128 = 0;
    for b in bytes {
        v = (v << 8) | *b as u128;
    }
    let mut out = [0u8; 26];
    for i in (0..26).rev() {
        out[i] = ALPHABET[(v & 31) as usize];
        v >>= 5;
    }
    String::from_utf8(out.to_vec()).unwrap()
}

/// Reads frames until the socket closes: [u32 len][u8 kind][16 id][u16 resultIndex][payload]. A length outside
/// [FRAME_HEADER_LEN, MAX_FRAME_LEN], or a kind the sidecar never sends (R1-4, read with the 19-byte header first), is a
/// stream that lost its framing: an InvalidData error before the payload is allocated, and the caller reconnects (the
/// sidecar drops a client whose frame it could not finish, §5). `generation`: the engine this socket belongs to
/// (`Relay::generation` when it was spawned); once the relay was cleared for a newer one, frames go nowhere.
pub async fn read_frames<R: AsyncRead + Unpin>(relay: &Relay, generation: u64, mut stream: R) -> std::io::Result<()> {
    let mut len_buf = [0u8; 4];
    let mut header = [0u8; FRAME_HEADER_LEN];
    loop {
        stream.read_exact(&mut len_buf).await?;
        let len = u32::from_le_bytes(len_buf) as usize;
        if !(FRAME_HEADER_LEN..=MAX_FRAME_LEN).contains(&len) {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                format!("a frame of {len} bytes is outside 19..={MAX_FRAME_LEN}: the results stream lost its framing"),
            ));
        }
        stream.read_exact(&mut header).await?;
        let kind = header[0];
        if !(KIND_SCHEMA..=KIND_END_OF_EXECUTION).contains(&kind) {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                format!("a frame of kind 0x{kind:02X} is not one the engine sends: the results stream lost its framing"),
            ));
        }
        let exec_id = ulid_string(&header[1..17]);
        // The webview gets [kind][resultIndex u16][payload]; the id is implied by the call.
        let mut frame = Vec::with_capacity(3 + len - FRAME_HEADER_LEN);
        frame.push(kind);
        frame.extend_from_slice(&header[17..]);
        frame.resize(3 + len - FRAME_HEADER_LEN, 0);
        stream.read_exact(&mut frame[3..]).await?;
        relay.push(generation, exec_id, kind, frame).await;
    }
}

/// Next frame of an execution as raw bytes (never JSON, §15 rule 4). Empty response = nothing yet; poll again.
/// `[0xFE, 0, 0]` = the run's frames were dropped; stop polling.
#[tauri::command]
pub async fn results_next(execution_id: String, relay: State<'_, Relay>) -> Result<Response, String> {
    Ok(Response::new(relay.next(&execution_id, POLL_WAIT).await))
}

/// Drops buffered and future frames of an execution (tab closed or result discarded).
#[tauri::command]
pub fn results_discard(execution_id: String, relay: State<'_, Relay>) {
    relay.discard(execution_id);
}

/// The error of a profile.delete whose profile is gone but whose keyring entries could not all be removed (L2).
fn secrets_not_deleted(e: &str) -> Value {
    json!({
        "code": -32001,
        "message": format!("The profile was deleted, but its saved password could not be removed from the OS keyring ({e}). Remove the \"querithm\" entries for it with the system's password manager."),
        "data": { "code": "secretsNotDeleted" },
    })
}

/// Forwards one JSON-RPC call verbatim. The only additions: for session.open and profile.test the
/// keyring secrets (database, and SSH tunnel) are injected here, so the sidecar never reads a secret at rest
/// (§8 Secrets), and import.*
/// calls get the path of their dialog-picked file.
#[tauri::command]
pub async fn rpc(
    method: String,
    params: Option<Value>,
    sup: State<'_, Supervisor>,
    files: State<'_, crate::files::PickedFiles>,
    grants: State<'_, crate::files::DbGrants>,
) -> Result<Value, Value> {
    let mut params = params.unwrap_or(Value::Null);
    // M1: no shell-owned name from the webview, and no other spelling of one, before anything is read or injected.
    crate::injection::refuse_shell_keys(&mut params)?;
    // import.*: the token from the native dialog becomes the path; a path sent by the webview never reaches the sidecar.
    // import.run holds the token until the reply: only an import that started spends it (not a Production guard block).
    let run_token = crate::files::inject(&method, &mut params, &files)?;
    // Phase D0c: only the shell names a database file. A fileGrant from the webview is dropped on every method; a
    // profile.save may bind only an unbound grant or its own.
    if let Some(obj) = params.as_object_mut() {
        obj.remove("fileGrant");
    }
    let bind = if method == "profile.save" { crate::injection::check_save(&params, &grants.registry())? } else { None };
    let profile_id = params.get("profileId").and_then(Value::as_str).map(str::to_owned);
    if method == "session.open" || method == "profile.test" {
        // Phase D0a: only the entries the sign-in needs are read, so a profile without a secret opens with a locked or
        // unavailable keyring. profile.test decides from the edited profile it carries, session.open from the stored one.
        let needs = crate::injection::needs_for(&method, &params);
        crate::injection::inject_file_grant(&mut params, &needs, &grants.registry())?;
        if let Some(obj) = params.as_object_mut() {
            // The database secret under "<profileId>", the SSH password/passphrase under "<profileId>:ssh".
            for (field, suffix, needed) in [("secret", "", needs.db_secret), ("sshSecret", ":ssh", needs.ssh_secret)] {
                let has = obj.get(field).map(|s| !s.is_null()).unwrap_or(false);
                if has || !needed {
                    continue;
                }
                if let Some(Value::String(pid)) = obj.get("profileId").cloned() {
                    match crate::keyring::get(format!("{pid}{suffix}")).await {
                        Ok(Some(secret)) => {
                            obj.insert(field.into(), Value::String(secret));
                        }
                        Ok(None) => {}
                        Err(e) => return Err(json!({ "code": -32001, "message": e, "data": { "code": "keyring" } })),
                    }
                }
            }
            // M21: whatever secret the engine gets here never appears in the shell's log (its relayed stderr included).
            for field in ["secret", "sshSecret"] {
                if let Some(Value::String(secret)) = obj.get(field) {
                    crate::shell_log::add_secret(secret);
                }
            }
        }
    }
    let reply = sup.call(&method, params).await;
    if let Ok(value) = &reply {
        let profiles = if method == "profile.list" { value.as_array().cloned().unwrap_or_default() } else if method == "profile.save" { vec![value.clone()] } else { vec![] };
        for profile in profiles {
            crate::injection::remember(&profile);
            crate::keyring::configure(&profile, method == "profile.save").await.map_err(|e| json!({ "code": -32001, "message": e, "data": { "code": "keyring" } }))?;
        }
    }
    if reply.is_ok() {
        match method.as_str() {
            "profile.save" => {
                if let (Some(grant), Some(id)) = (bind, reply.as_ref().ok().and_then(|v| v.get("id")).and_then(Value::as_str)) {
                    grants.bind(&grant, id);
                }
            }
            // A deleted profile takes its grants along; a revoked grant is gone until the file is picked again.
            "profile.delete" | "grant.revoke" => {
                if let Some(pid) = profile_id.as_deref() {
                    grants.remove_profile(pid);
                    if method == "profile.delete" {
                        crate::injection::forget(pid);
                        // L2: and its keyring entries (database and SSH, indefinite and timed) with their memory copies. The
                        // sidecar has deleted the profile by now, so a failure is reported as such, not as a failed delete.
                        if let Err(e) = crate::keyring::forget_profile(pid).await {
                            return Err(secrets_not_deleted(&e));
                        }
                    }
                }
            }
            _ => {}
        }
    }
    if let Some(token) = run_token {
        files.settle_run(&token, crate::files::run_started(&reply));
    }
    reply
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    const A: &str = "01AAAAAAAAAAAAAAAAAAAAAAAA";
    const B: &str = "01BBBBBBBBBBBBBBBBBBBBBBBB";

    fn batch(n: u8) -> Vec<u8> {
        vec![KIND_BATCH, 0, 0, n]
    }

    fn end() -> Vec<u8> {
        vec![KIND_END_OF_EXECUTION, 0, 0]
    }

    async fn push(relay: &Relay, id: &str, frame: Vec<u8>) {
        relay.push(relay.generation(), id.to_owned(), frame[0], frame).await;
    }

    fn entries(relay: &Relay) -> usize {
        relay.queues.lock().unwrap().by_id.len()
    }

    /// [len][kind][16-byte id][resultIndex][payload], the sidecar's framing.
    fn wire(kind: u8, id: &[u8; 16], payload: &[u8]) -> Vec<u8> {
        let mut out = ((FRAME_HEADER_LEN + payload.len()) as u32).to_le_bytes().to_vec();
        out.push(kind);
        out.extend_from_slice(id);
        out.extend_from_slice(&[0, 0]);
        out.extend_from_slice(payload);
        out
    }

    #[test]
    fn a_keyring_failure_after_delete_says_the_profile_is_gone() {
        let e = secrets_not_deleted("locked");
        assert_eq!(e["data"]["code"], "secretsNotDeleted");
        assert_eq!(e["code"], -32001);
        assert!(e["message"].as_str().unwrap().starts_with("The profile was deleted"));
    }

    #[tokio::test]
    async fn eight_batches_are_buffered_then_the_reader_waits_until_one_is_taken() {
        let relay = Arc::new(Relay::with_stale_after(Duration::from_secs(60)));
        for n in 0..8 {
            push(&relay, A, batch(n)).await;
        }
        let r = relay.clone();
        let ninth = tokio::spawn(async move { push(&r, A, batch(8)).await });
        tokio::time::sleep(Duration::from_millis(100)).await;
        assert!(!ninth.is_finished(), "backpressure: the ninth batch waits for space");
        assert_eq!(relay.next(A, Duration::from_secs(1)).await, batch(0));
        tokio::time::timeout(Duration::from_secs(5), ninth).await.expect("space frees the reader").unwrap();
        for n in 1..9 {
            assert_eq!(relay.next(A, Duration::from_secs(1)).await, batch(n));
        }
        // A non-batch frame never waits on the cap.
        push(&relay, A, end()).await;
        assert_eq!(relay.next(A, Duration::from_secs(1)).await, end());
        assert_eq!(entries(&relay), 0);
    }

    #[tokio::test]
    async fn a_full_queue_nobody_polls_is_dropped_after_the_bound_and_other_executions_keep_flowing() {
        let relay = Relay::with_stale_after(Duration::from_millis(200));
        // From before the first push: the queue's clock starts with it, so the drop can never come sooner than this.
        let started = Instant::now();
        for n in 0..8 {
            push(&relay, A, batch(n)).await;
        }
        // Before the guard this await never returned: the one socket reader stalled every execution.
        tokio::time::timeout(Duration::from_secs(5), push(&relay, A, batch(8))).await.expect("the reader is not held forever");
        let took = started.elapsed();
        assert!(took >= Duration::from_millis(200), "{took:?}");

        push(&relay, B, batch(1)).await;
        push(&relay, B, end()).await;
        assert_eq!(relay.next(B, Duration::from_secs(1)).await, batch(1));
        assert_eq!(relay.next(B, Duration::from_secs(1)).await, end());

        // The dropped run's later frames go nowhere, and a consumer that comes back is told instead of waiting forever.
        push(&relay, A, batch(9)).await;
        assert_eq!(relay.next(A, Duration::from_secs(1)).await, vec![KIND_GONE, 0, 0]);
        push(&relay, A, end()).await;
        assert_eq!(entries(&relay), 0);
        assert_eq!(relay.next(A, Duration::from_secs(1)).await, vec![KIND_GONE, 0, 0]);
    }

    #[tokio::test]
    async fn a_consumer_that_keeps_reading_a_full_queue_is_never_cut_off() {
        // Polls 20 ms apart against a 2 s bound: a loaded runner's jitter is far inside the margin.
        let relay = Arc::new(Relay::with_stale_after(Duration::from_secs(2)));
        let r = relay.clone();
        let producer = tokio::spawn(async move {
            for n in 0..30 {
                push(&r, A, batch(n)).await;
            }
            push(&r, A, end()).await;
        });
        let mut got = Vec::new();
        loop {
            // Slower than the producer (the queue stays full), but each poll comes well within the bound.
            tokio::time::sleep(Duration::from_millis(20)).await;
            let frame = relay.next(A, Duration::from_secs(2)).await;
            assert_ne!(frame, vec![KIND_GONE, 0, 0], "an active consumer was dropped");
            if frame == end() {
                break;
            }
            got.push(frame[3]);
        }
        assert_eq!(got, (0..30).collect::<Vec<u8>>());
        producer.await.unwrap();
    }

    #[tokio::test]
    async fn results_discard_frees_a_paused_reader_and_drops_the_run_until_its_end() {
        let relay = Arc::new(Relay::with_stale_after(Duration::from_secs(60)));
        for n in 0..8 {
            push(&relay, A, batch(n)).await;
        }
        let r = relay.clone();
        let ninth = tokio::spawn(async move { push(&r, A, batch(8)).await });
        tokio::time::sleep(Duration::from_millis(50)).await;
        relay.discard(A.to_owned());
        tokio::time::timeout(Duration::from_secs(5), ninth).await.expect("discard frees the reader").unwrap();
        push(&relay, A, batch(9)).await;
        assert_eq!(relay.queues.lock().unwrap().by_id[A].frames.len(), 0);
        push(&relay, A, end()).await;
        assert_eq!(entries(&relay), 0);
    }

    #[tokio::test]
    async fn results_discard_after_the_end_leaves_no_entry() {
        let relay = Relay::with_stale_after(Duration::from_secs(60));
        // End consumed, then discarded (a tab that closes long after its run): no entry is made for it.
        push(&relay, A, end()).await;
        assert_eq!(relay.next(A, Duration::from_secs(1)).await, end());
        relay.discard(A.to_owned());
        assert_eq!(entries(&relay), 0);
        // End buffered but not consumed, then discarded.
        push(&relay, B, batch(1)).await;
        push(&relay, B, end()).await;
        relay.discard(B.to_owned());
        assert_eq!(entries(&relay), 0);
        // Discarded before its first frame: the entry drops its frames and goes with its end.
        relay.discard("01CCCCCCCCCCCCCCCCCCCCCCCC".to_owned());
        push(&relay, "01CCCCCCCCCCCCCCCCCCCCCCCC", batch(1)).await;
        push(&relay, "01CCCCCCCCCCCCCCCCCCCCCCCC", end()).await;
        assert_eq!(entries(&relay), 0);
    }

    #[tokio::test]
    async fn an_unknown_run_waits_then_answers_nothing_yet() {
        let relay = Relay::with_stale_after(Duration::from_secs(60));
        assert_eq!(relay.next(A, Duration::from_millis(50)).await, Vec::<u8>::new());
        assert_eq!(entries(&relay), 0);
    }

    #[tokio::test]
    async fn read_frames_refuses_an_implausible_length_before_reading_or_allocating_it() {
        let relay = Relay::with_stale_after(Duration::from_secs(60));
        for len in [u32::MAX as usize, MAX_FRAME_LEN + 1, FRAME_HEADER_LEN - 1] {
            // Only the length is there: a reader that trusted it would allocate it and then fail on the missing bytes.
            let bytes = (len as u32).to_le_bytes();
            let err = read_frames(&relay, relay.generation(), &bytes[..]).await.unwrap_err();
            assert_eq!(err.kind(), std::io::ErrorKind::InvalidData, "length {len}: {err}");
        }
        // The largest frame the sidecar may send is accepted (its header only: the read then ends on the missing payload).
        let bytes = (MAX_FRAME_LEN as u32).to_le_bytes();
        assert_eq!(read_frames(&relay, relay.generation(), &bytes[..]).await.unwrap_err().kind(), std::io::ErrorKind::UnexpectedEof);
    }

    #[tokio::test]
    async fn read_frames_refuses_a_kind_the_engine_never_sends_before_allocating_the_payload() {
        // R1-4: the largest length with a kind byte that is no frame kind: refused after the 19-byte header, so the
        // 256 MiB it names is never allocated (a reader that trusted it would then fail on the missing payload instead).
        let relay = Relay::with_stale_after(Duration::from_secs(60));
        for kind in [0x00u8, 0x05, KIND_GONE, 0xFF] {
            let mut bytes = (MAX_FRAME_LEN as u32).to_le_bytes().to_vec();
            bytes.push(kind);
            bytes.extend_from_slice(&[0u8; 18]);
            let err = read_frames(&relay, relay.generation(), &bytes[..]).await.unwrap_err();
            assert_eq!(err.kind(), std::io::ErrorKind::InvalidData, "kind {kind:#x}: {err}");
        }
        for kind in [KIND_SCHEMA, KIND_BATCH, 0x03, KIND_END_OF_EXECUTION] {
            let mut bytes = (MAX_FRAME_LEN as u32).to_le_bytes().to_vec();
            bytes.push(kind);
            bytes.extend_from_slice(&[0u8; 18]);
            assert_eq!(read_frames(&relay, relay.generation(), &bytes[..]).await.unwrap_err().kind(), std::io::ErrorKind::UnexpectedEof);
        }
    }

    #[tokio::test]
    async fn after_clear_the_old_engines_reader_makes_no_entries() {
        // R1-3: a killed engine's reader still drains its socket; its frames made entries nobody would ever free.
        let relay = Relay::with_stale_after(Duration::from_secs(60));
        let old = relay.generation();
        relay.push(old, A.to_owned(), KIND_BATCH, batch(1)).await;
        relay.clear();
        for n in 0..20 {
            relay.push(old, format!("01OLD{n:021}"), KIND_BATCH, batch(n)).await;
        }
        relay.push(old, A.to_owned(), KIND_END_OF_EXECUTION, end()).await;
        assert_eq!(entries(&relay), 0);
        // The new engine's reader goes on as usual.
        push(&relay, B, batch(2)).await;
        assert_eq!(relay.next(B, Duration::from_secs(1)).await, batch(2));
        // And read_frames of the old socket drops what it reads.
        let bytes = wire(KIND_BATCH, &[3u8; 16], b"late");
        let _ = read_frames(&relay, old, &bytes[..]).await;
        assert_eq!(relay.next(&ulid_string(&[3u8; 16]), Duration::from_millis(20)).await, Vec::<u8>::new());
        assert_eq!(entries(&relay), 1, "B only");
    }

    #[tokio::test]
    async fn entries_nobody_polls_go_after_the_bound_even_when_their_queue_never_fills() {
        // R1-3: a vanished consumer of a run with a few frames, and an end nobody read, stayed for the app's life.
        let relay = Relay::with_stale_after(Duration::from_millis(100));
        push(&relay, A, batch(1)).await;
        push(&relay, B, batch(1)).await;
        push(&relay, B, end()).await;
        tokio::time::sleep(Duration::from_millis(150)).await;
        // Any later push (or poll) sweeps them; their line is logged after the lock is released (R1-5).
        let mut logs = Vec::new();
        let mut frame = Some(batch(1));
        assert!(matches!(relay.try_push(relay.generation(), "01CCCCCCCCCCCCCCCCCCCCCCCC", KIND_BATCH, &mut frame, &mut logs), Push::Done(true)));
        assert_eq!(entries(&relay), 1, "only the new run is held");
        assert!(logs.iter().any(|l| l.contains(A)) && logs.iter().any(|l| l.contains(B)), "{logs:?}");
        // Their later frames go nowhere, and a consumer that comes back is told instead of waiting.
        push(&relay, A, batch(2)).await;
        assert_eq!(entries(&relay), 1);
        assert_eq!(relay.next(A, Duration::from_secs(1)).await, vec![KIND_GONE, 0, 0]);
        // A consumer that keeps polling within the bound keeps its entry.
        for _ in 0..4 {
            tokio::time::sleep(Duration::from_millis(40)).await;
            let _ = relay.next("01CCCCCCCCCCCCCCCCCCCCCCCC", Duration::from_millis(1)).await;
        }
        push(&relay, "01DDDDDDDDDDDDDDDDDDDDDDDD", batch(1)).await;
        assert!(relay.queues.lock().unwrap().by_id.contains_key("01CCCCCCCCCCCCCCCCCCCCCCCC"), "a reading consumer was cut off");
    }

    #[tokio::test]
    async fn read_frames_keeps_the_whole_frames_of_a_torn_stream_and_drops_the_cut_one() {
        let relay = Relay::with_stale_after(Duration::from_secs(60));
        let a = [1u8; 16];
        let b = [2u8; 16];
        let mut bytes = wire(0x01, &a, b"schema");
        let torn = wire(KIND_BATCH, &b, &[7u8; 1000]);
        bytes.extend_from_slice(&torn[..4 + FRAME_HEADER_LEN + 10]);
        let err = read_frames(&relay, relay.generation(), &bytes[..]).await.unwrap_err();
        assert_eq!(err.kind(), std::io::ErrorKind::UnexpectedEof);
        let mut want = vec![0x01, 0, 0];
        want.extend_from_slice(b"schema");
        assert_eq!(relay.next(&ulid_string(&a), Duration::from_secs(1)).await, want);
        assert_eq!(relay.next(&ulid_string(&b), Duration::from_millis(50)).await, Vec::<u8>::new());
    }

    #[test]
    fn ulid_bytes_become_the_sidecar_s_crockford_text() {
        assert_eq!(ulid_string(&[0u8; 16]), "00000000000000000000000000");
        assert_eq!(ulid_string(&[0xFF; 16]), "7ZZZZZZZZZZZZZZZZZZZZZZZZZ");
    }
}
