// The shell's own log (M21): `<data dir>/querithm/logs/shell-YYYYMMDD.log`. Release builds have no console on Windows,
// so what the shell knows about a failure — relayed sidecar stderr (a crash the sidecar could not log itself), data-plane
// failures, a spawn error, a panic — goes here, scrubbed. The same rule as the sidecar's logs (Host/Diagnostics.cs
// LogFiles): a file stops at MAX_FILE_BYTES and the next part starts; files last written within KEEP_DAYS days are kept,
// at most MAX_FILES of them. Lines are appended with O_APPEND / FILE_APPEND_DATA in one write each, so two app
// instances sharing the file never cut into each other's lines. The launch token and every secret the shell handed the
// sidecar are replaced by `***` — written as they are, JSON-escaped (`\"`, `\\`, `\uXXXX`) or URL-encoded (`%XX`, `+`
// for a space), in any mix (R1-10) — and so is any `password=` / `pwd=` / `secret` / `token` value. A secret shorter
// than MIN_SECRET characters is not matched as such (it would mask ordinary text everywhere); the key=value rules still
// cover it. The newest MAX_TOKENS launch tokens are always kept; of the other secrets, the MAX_SECRETS handed most
// recently are (one handed again moves to the newest place), so a secret still in use is never dropped for a stale one.
use std::collections::VecDeque;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const MAX_FILE_BYTES: u64 = 5 * 1024 * 1024;
pub const KEEP_DAYS: u64 = 7;
pub const MAX_FILES: usize = 14;
/// A line longer than this is cut: the log is for diagnosis, not for copies of whatever a native library printed.
const MAX_LINE: usize = 4000;
/// Secrets remembered for scrubbing; past this, the one handed least recently goes.
const MAX_SECRETS: usize = 256;
/// Launch tokens kept apart from the secrets, never pushed out by them: the current engine's and a few before it.
const MAX_TOKENS: usize = 4;
/// Shorter secrets are not matched as such (see the module comment).
pub const MIN_SECRET: usize = 3;

#[derive(Default)]
struct State {
    dir: Option<PathBuf>,
    tokens: VecDeque<String>,
    /// Least recently handed first.
    secrets: VecDeque<String>,
}

impl State {
    fn add_secret(&mut self, secret: &str) {
        if secret.chars().count() >= MIN_SECRET {
            remember(&mut self.secrets, secret, MAX_SECRETS);
        }
    }

    fn add_token(&mut self, token: &str) {
        if token.chars().count() >= MIN_SECRET {
            remember(&mut self.tokens, token, MAX_TOKENS);
        }
    }

    fn all_secrets(&self) -> Vec<String> {
        self.tokens.iter().chain(self.secrets.iter()).cloned().collect()
    }

    /// Where lines go. Unit tests write only where their own thread said (a test of the global log must not catch, or
    /// be broken by, the lines other tests write meanwhile).
    fn log_dir(&self) -> Option<PathBuf> {
        #[cfg(test)]
        {
            TEST_DIR.with(|d| d.borrow().clone())
        }
        #[cfg(not(test))]
        {
            self.dir.clone().or_else(fallback_dir)
        }
    }
}

#[cfg(test)]
thread_local! {
    static TEST_DIR: std::cell::RefCell<Option<PathBuf>> = const { std::cell::RefCell::new(None) };
}

fn state() -> &'static Mutex<State> {
    static STATE: OnceLock<Mutex<State>> = OnceLock::new();
    STATE.get_or_init(|| Mutex::new(State::default()))
}

/// `<data dir>/querithm/logs` from the environment, the way Tauri's `data_dir()` resolves it: what the panic hook uses
/// before setup has given the real one.
pub fn default_dir() -> Option<PathBuf> {
    let base = if cfg!(windows) {
        std::env::var_os("APPDATA").map(PathBuf::from)
    } else if cfg!(target_os = "macos") {
        std::env::var_os("HOME").map(|h| PathBuf::from(h).join("Library").join("Application Support"))
    } else {
        std::env::var_os("XDG_DATA_HOME").filter(|v| !v.is_empty()).map(PathBuf::from).or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".local").join("share")))
    };
    base.map(|b| b.join("querithm").join("logs"))
}

/// Before setup gave the folder: the default one; never in unit tests, which must not write into the user's folders.
#[cfg_attr(test, allow(dead_code))]
fn fallback_dir() -> Option<PathBuf> {
    if cfg!(test) {
        None
    } else {
        default_dir()
    }
}

/// Points the log at `dir` (setup, from Tauri's path resolver) and applies the retention rule once.
pub fn init(dir: PathBuf) {
    prune(&dir, SystemTime::now(), None);
    state().lock().unwrap_or_else(|e| e.into_inner()).dir = Some(dir);
}

/// Remembers a secret (a password the shell sent in session.open) so no line ever carries it. Handed again, it becomes
/// the newest: the secrets kept are the MAX_SECRETS handed most recently.
pub fn add_secret(secret: &str) {
    state().lock().unwrap_or_else(|e| e.into_inner()).add_secret(secret);
}

/// Remembers an engine's launch token: kept apart, so no number of secrets handed afterwards pushes it out.
pub fn add_token(token: &str) {
    state().lock().unwrap_or_else(|e| e.into_inner()).add_token(token);
}

/// Moves `secret` to the newest place of `list` (adding it), dropping the oldest past `max`.
fn remember(list: &mut VecDeque<String>, secret: &str, max: usize) {
    if let Some(at) = list.iter().position(|k| k == secret) {
        let kept = list.remove(at).unwrap();
        list.push_back(kept);
        return;
    }
    while list.len() >= max {
        list.pop_front();
    }
    list.push_back(secret.to_owned());
}

/// Tests: whether a secret is remembered (as a secret or a token).
#[cfg(test)]
pub fn remembers(secret: &str) -> bool {
    state().lock().unwrap_or_else(|e| e.into_inner()).all_secrets().iter().any(|k| k == secret)
}

/// Writes one scrubbed line. Never fails the caller: a log that cannot be written is skipped.
pub fn write(level: &str, message: &str) {
    let (dir, secrets) = {
        let s = state().lock().unwrap_or_else(|e| e.into_inner());
        (s.log_dir(), s.all_secrets())
    };
    if let Some(dir) = dir {
        let _ = write_line(&dir, SystemTime::now(), level, &scrub(message, &secrets));
    }
    #[cfg(debug_assertions)]
    eprintln!("[{level}] {message}");
}

/// A panic leaves a crash record (where, what, which version) before `panic = "abort"` ends the process. Called first in
/// main, before anything can panic; the default hook still prints to stderr afterwards.
pub fn install_panic_hook() {
    let default = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let what = info
            .payload()
            .downcast_ref::<&str>()
            .map(|s| s.to_string())
            .or_else(|| info.payload().downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "(no message)".into());
        let at = info.location().map(|l| format!("{}:{}", l.file(), l.line())).unwrap_or_else(|| "unknown location".into());
        // try_lock: a panic while the log itself held its lock must not deadlock the hook.
        let (dir, secrets) = match state().try_lock() {
            Ok(s) => (s.log_dir(), s.all_secrets()),
            Err(_) if cfg!(test) => (None, Vec::new()),
            Err(_) => (fallback_dir(), Vec::new()),
        };
        if let Some(dir) = dir {
            let line = format!("panic at {at}: {what} (Querithm {})", env!("CARGO_PKG_VERSION"));
            let _ = write_line(&dir, SystemTime::now(), "crash", &scrub(&line, &secrets));
        }
        default(info);
    }));
}

/// The secret values and every value of a name ending in `password` / `passphrase` / `pwd` / `secret` / `token` /
/// `authorization` (`=` or `:`, quoted or bare, JSON field names such as `"sshSecret"` included) become `***`.
pub fn scrub(line: &str, secrets: &[String]) -> String {
    const KEYS: [&str; 6] = ["password", "passphrase", "pwd", "secret", "token", "authorization"];
    let lower = line.to_ascii_lowercase();
    let b = line.as_bytes();
    let word = |c: u8| c.is_ascii_alphanumeric() || c == b'_';
    let mut out = String::with_capacity(line.len());
    let mut i = 0;
    while i < b.len() {
        if word(b[i]) && (i == 0 || !word(b[i - 1])) {
            let mut j = i;
            while j < b.len() && word(b[j]) {
                j += 1;
            }
            if let Some(end) = KEYS.iter().any(|k| lower[i..j].ends_with(k)).then(|| value_end(&lower, j)).flatten() {
                out.push_str(&line[i..end.0]);
                out.push_str("***");
                i = end.1;
                continue;
            }
            out.push_str(&line[i..j]);
            i = j;
            continue;
        }
        let c = line[i..].chars().next().unwrap();
        out.push(c);
        i += c.len_utf8();
    }
    for s in secrets {
        if s.chars().count() >= MIN_SECRET {
            out = mask(&out, s);
        }
    }
    if out.len() > MAX_LINE {
        let mut cut = MAX_LINE;
        while !out.is_char_boundary(cut) {
            cut -= 1;
        }
        out.truncate(cut);
        out.push('…');
    }
    out.replace(['\r', '\n'], " ")
}

/// `text` with every occurrence of `secret` — raw, JSON-escaped or URL-encoded, character by character — as `***`.
fn mask(text: &str, secret: &str) -> String {
    let b = text.as_bytes();
    let first = secret.as_bytes()[0];
    let mut out = String::with_capacity(text.len());
    let (mut i, mut last) = (0, 0);
    while i < b.len() {
        // Only where the secret's first byte, or an escape that may spell it, stands: a match always starts on a
        // character boundary (a UTF-8 lead byte or ASCII never equals a continuation byte).
        if matches!(b[i], b'%' | b'\\' | b'+') || b[i] == first {
            if let Some(end) = match_at(b, i, secret) {
                out.push_str(&text[last..i]);
                out.push_str("***");
                i = end;
                last = end;
                continue;
            }
        }
        i += 1;
    }
    out.push_str(&text[last..]);
    out
}

/// Where `secret` spelled from byte `at` ends, if it does: each character raw, as its UTF-8 bytes in `%XX` (any case;
/// a space also as `+`), or JSON-escaped (`\"` `\\` `\/` `\n` `\r` `\t` `\b` `\f`, or `\uXXXX`, a surrogate pair
/// beyond the BMP). A character may match more than one way (a raw `\` is also the start of `\\`), so every way is
/// followed: the positions reachable after each character, at most one entry per byte offset.
fn match_at(line: &[u8], at: usize, secret: &str) -> Option<usize> {
    let mut here = vec![at];
    for c in secret.chars() {
        let mut next: Vec<usize> = Vec::new();
        for &i in &here {
            for end in spellings(line, i, c).into_iter().flatten() {
                if !next.contains(&end) {
                    next.push(end);
                }
            }
        }
        if next.is_empty() {
            return None;
        }
        here = next;
    }
    here.into_iter().max()
}

/// Where `c` written from byte `i` ends, for each spelling that is there: raw, URL-encoded, JSON-escaped.
fn spellings(line: &[u8], i: usize, c: char) -> [Option<usize>; 3] {
    let mut buf = [0u8; 4];
    let raw = c.encode_utf8(&mut buf).as_bytes();
    let plain = (line.get(i..i + raw.len()) == Some(raw)).then_some(i + raw.len());
    let url = match line.get(i) {
        Some(b'%') => {
            let mut j = Some(i);
            for &byte in raw {
                j = j.filter(|&j| line.get(j) == Some(&b'%') && line.get(j + 1..j + 3).and_then(hex) == Some(u32::from(byte))).map(|j| j + 3);
            }
            j
        }
        Some(b'+') if c == ' ' => Some(i + 1),
        _ => None,
    };
    let json = if line.get(i) == Some(&b'\\') {
        let short = match c {
            '"' => Some(b'"'),
            '\\' => Some(b'\\'),
            '/' => Some(b'/'),
            '\n' => Some(b'n'),
            '\r' => Some(b'r'),
            '\t' => Some(b't'),
            '\u{8}' => Some(b'b'),
            '\u{c}' => Some(b'f'),
            _ => None,
        };
        if short.is_some() && line.get(i + 1).copied() == short {
            Some(i + 2)
        } else {
            let mut units = [0u16; 2];
            let mut j = Some(i);
            for &u in c.encode_utf16(&mut units).iter() {
                j = j.filter(|&j| line.get(j..j + 2) == Some(b"\\u".as_slice()) && line.get(j + 2..j + 6).and_then(hex) == Some(u32::from(u))).map(|j| j + 6);
            }
            j
        }
    } else {
        None
    };
    [plain, url, json]
}

/// Hex digits only (no sign, no prefix), any case.
fn hex(digits: &[u8]) -> Option<u32> {
    if !digits.iter().all(u8::is_ascii_hexdigit) {
        return None;
    }
    u32::from_str_radix(std::str::from_utf8(digits).ok()?, 16).ok()
}

/// After a secret's name at byte `j`: where its value starts and ends, if `=` or `:` follows (a closing quote of a JSON
/// name and spaces may come first; an Authorization scheme word stays).
fn value_end(lower: &str, mut j: usize) -> Option<(usize, usize)> {
    let b = lower.as_bytes();
    if j < b.len() && (b[j] == b'"' || b[j] == b'\'') {
        j += 1;
    }
    while j < b.len() && b[j] == b' ' {
        j += 1;
    }
    if j >= b.len() || (b[j] != b'=' && b[j] != b':') {
        return None;
    }
    j += 1;
    while j < b.len() && b[j] == b' ' {
        j += 1;
    }
    for scheme in ["bearer ", "basic "] {
        if lower[j..].starts_with(scheme) {
            j += scheme.len();
        }
    }
    let end = match b.get(j) {
        Some(&q) if q == b'"' || q == b'\'' => {
            let mut k = j + 1;
            while k < b.len() && b[k] != q {
                k += if b[k] == b'\\' { 2 } else { 1 };
            }
            (k + 1).min(b.len())
        }
        _ => {
            let mut k = j;
            while k < b.len() && !matches!(b[k], b';' | b' ' | b',' | b'}' | b'"' | b'\'' | b'\t') {
                k += 1;
            }
            k
        }
    };
    let mut end = end;
    while !lower.is_char_boundary(end) {
        end += 1;
    }
    Some((j, end))
}

/// (year, month, day) of a UTC day number (days since 1970-01-01), the civil-from-days algorithm.
fn civil(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (yoe + era * 400 + i64::from(m <= 2), m, d)
}

fn stamp(now: SystemTime) -> (String, String) {
    let secs = now.duration_since(UNIX_EPOCH).unwrap_or_default().as_secs() as i64;
    let (y, m, d) = civil(secs.div_euclid(86_400));
    let t = secs.rem_euclid(86_400);
    (format!("{y:04}{m:02}{d:02}"), format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", t / 3600, t / 60 % 60, t % 60))
}

/// Part `part` of the day's file.
fn file_name(day: &str, part: u32) -> String {
    if part <= 1 {
        format!("shell-{day}.log")
    } else {
        format!("shell-{day}-{part}.log")
    }
}

/// Appends `<time> [level] message` to the first part of the day under the cap; a new file prunes the old ones.
fn write_line(dir: &Path, now: SystemTime, level: &str, message: &str) -> std::io::Result<PathBuf> {
    let (day, time) = stamp(now);
    let mut part = 1;
    let path = loop {
        let path = dir.join(file_name(&day, part));
        match fs::metadata(&path) {
            Ok(m) if m.len() >= MAX_FILE_BYTES => part += 1,
            _ => break path,
        }
    };
    let fresh = !path.exists();
    if fresh {
        make_dir(dir)?;
    }
    let mut options = OpenOptions::new();
    options.create(true).append(true);
    #[cfg(unix)]
    std::os::unix::fs::OpenOptionsExt::mode(&mut options, 0o600);
    let mut file = options.open(&path)?;
    file.write_all(format!("{time} [{level}] {message}\n").as_bytes())?;
    if fresh {
        prune(dir, now, Some(&path));
    }
    Ok(path)
}

fn make_dir(dir: &Path) -> std::io::Result<()> {
    let mut b = fs::DirBuilder::new();
    b.recursive(true);
    #[cfg(unix)]
    std::os::unix::fs::DirBuilderExt::mode(&mut b, 0o700);
    b.create(dir)
}

/// The retention rule over `shell-*.log`: newest first, at most MAX_FILES, none older than KEEP_DAYS; `keep` always stays.
fn prune(dir: &Path, now: SystemTime, keep: Option<&Path>) {
    let Ok(entries) = fs::read_dir(dir) else { return };
    let mut files: Vec<(PathBuf, SystemTime)> = entries
        .filter_map(Result::ok)
        .filter(|e| e.file_name().to_str().is_some_and(|n| n.starts_with("shell-") && n.ends_with(".log")))
        .filter_map(|e| Some((e.path(), e.metadata().ok().filter(|m| m.is_file())?.modified().ok()?)))
        .collect();
    files.sort_by(|a, b| b.1.cmp(&a.1));
    let max_age = Duration::from_secs(KEEP_DAYS * 86_400);
    for (i, (path, at)) in files.iter().enumerate() {
        let old = now.duration_since(*at).map(|age| age > max_age).unwrap_or(false);
        if Some(path.as_path()) != keep && (i >= MAX_FILES || old) {
            let _ = fs::remove_file(path);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch() -> PathBuf {
        let d = std::env::temp_dir().join(format!("qshell-log-{}-{:?}", std::process::id(), std::thread::current().id()));
        let _ = fs::remove_dir_all(&d);
        d
    }

    fn day(n: i64) -> SystemTime {
        UNIX_EPOCH + Duration::from_secs((n * 86_400) as u64)
    }

    #[test]
    fn days_and_times_are_utc_calendar_dates() {
        assert_eq!(stamp(UNIX_EPOCH).0, "19700101");
        // 2026-11-03 is day 20_760; 2024-02-29 is day 19_782 (a leap day).
        assert_eq!(stamp(day(20_760) + Duration::from_secs(3600 + 2 * 60 + 3)), ("20261103".into(), "2026-11-03T01:02:03Z".into()));
        assert_eq!(stamp(day(19_782)).0, "20240229");
        assert_eq!(stamp(day(19_783)).0, "20240301");
    }

    #[test]
    fn the_launch_token_and_known_secrets_are_never_written() {
        let dir = scratch();
        let token = "0123456789abcdef0123456789abcdef";
        let secrets = vec![token.to_string(), "Tr0ub4dor&3".to_string()];
        let line = scrub(&format!("[sidecar] Unhandled exception: token {token} for a login with Tr0ub4dor&3"), &secrets);
        let path = write_line(&dir, day(20_760), "sidecar", &line).unwrap();
        let text = fs::read_to_string(&path).unwrap();
        assert!(!text.contains(token) && !text.contains("Tr0ub4dor&3"), "{text}");
        assert!(text.starts_with("2026-11-03T00:00:00Z [sidecar] [sidecar] Unhandled exception: token *** for a login with ***"), "{text}");
        assert_eq!(path.file_name().unwrap(), "shell-20261103.log");
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn key_value_secrets_are_masked_whatever_their_spelling() {
        let s = |t: &str| scrub(t, &[]);
        assert_eq!(s("Server=x;Password=hunter2;Database=d"), "Server=x;Password=***;Database=d");
        assert_eq!(s("pwd = 'a b c' rest"), "pwd = *** rest");
        assert_eq!(s(r#"{"secret":"p\"q","sshSecret":"k","accessToken":"t","user":"u"}"#), r#"{"secret":***,"sshSecret":***,"accessToken":***,"user":"u"}"#);
        assert_eq!(s("Authorization: Bearer abc.def"), "Authorization: Bearer ***");
        assert_eq!(s("token=abc; dbPassword: zz"), "token=***; dbPassword: ***");
        // Not a secret: a name that does not end in one, or a name with no value.
        assert_eq!(s("passwordHash=1 tokens=2 secret here"), "passwordHash=1 tokens=2 secret here");
        assert_eq!(s("ünïcode password=ä€ ok"), "ünïcode password=*** ok");
        assert_eq!(s("two\nlines"), "two lines");
        assert!(s(&"x".repeat(MAX_LINE * 2)).chars().count() <= MAX_LINE + 1);
    }

    #[test]
    fn escaped_and_url_encoded_secrets_are_masked_too() {
        let secrets = vec![r#"p"a\s&s w/é"#.to_string()];
        let s = |t: &str| scrub(t, &secrets);
        // JSON as serde writes it, and as .NET's default encoder does (\uXXXX for &, " and non-ASCII).
        assert_eq!(s(r#"{"conn":"Host=x;Pwd2=p\"a\\s&s w/é;"}"#), r#"{"conn":"Host=x;Pwd2=***;"}"#);
        assert_eq!(s(r#"{"conn":"p\u0022a\\s\u0026s w\/\u00E9"}"#), r#"{"conn":"***"}"#);
        // A URL, fully or partly encoded, any hex case, + for the space.
        assert_eq!(s("postgres://u:p%22a%5Cs%26s%20w%2F%C3%A9@h/db"), "postgres://u:***@h/db");
        assert_eq!(s("postgres://u:p%22a%5cs&s+w/%c3%a9@h/db"), "postgres://u:***@h/db");
        // Something that only looks like it stays.
        assert_eq!(s("p%22a%5Cs%26s w/e"), "p%22a%5Cs%26s w/e");
        // Beyond the BMP: a surrogate pair.
        assert_eq!(scrub(r#"x "k\ud83d\ude00ey" y"#, &["k😀ey".to_string()]), r#"x "***" y"#);
        // Shorter than MIN_SECRET: not matched (it would mask ordinary text).
        assert_eq!(scrub("ab is short", &["ab".to_string()]), "ab is short");
    }

    #[test]
    fn a_secret_handed_again_is_kept_and_launch_tokens_are_never_pushed_out() {
        let mut list = VecDeque::new();
        for i in 0..MAX_SECRETS {
            remember(&mut list, &format!("secret-{i}"), MAX_SECRETS);
        }
        // secret-0 is handed again (a connection still in use): it is now the newest, and secret-1 goes first.
        remember(&mut list, "secret-0", MAX_SECRETS);
        remember(&mut list, "fresh", MAX_SECRETS);
        assert!(list.contains(&"secret-0".to_string()));
        assert!(!list.contains(&"secret-1".to_string()));
        assert_eq!(list.len(), MAX_SECRETS);
        // A launch token survives any number of secrets (a State of the test's own: the global one is shared).
        let mut st = State::default();
        st.add_token("launch-token-r1-10");
        for i in 0..(MAX_SECRETS + 10) {
            st.add_secret(&format!("flood-{i}"));
        }
        st.add_secret("ab");
        let all = st.all_secrets();
        assert!(all.contains(&"launch-token-r1-10".to_string()));
        assert!(!all.contains(&"flood-0".to_string()) && all.contains(&format!("flood-{}", MAX_SECRETS + 9)));
        assert!(!all.contains(&"ab".to_string()), "too short to remember");
        assert_eq!(all.len(), MAX_SECRETS + 1);
    }

    #[test]
    fn a_full_file_rolls_over_to_the_next_part_of_the_day() {
        let dir = scratch();
        make_dir(&dir).unwrap();
        let first = dir.join("shell-20261103.log");
        fs::write(&first, vec![b'x'; MAX_FILE_BYTES as usize]).unwrap();
        let path = write_line(&dir, day(20_760), "warn", "after the cap").unwrap();
        assert_eq!(path.file_name().unwrap(), "shell-20261103-2.log");
        assert_eq!(fs::metadata(&first).unwrap().len(), MAX_FILE_BYTES, "the full part is not written to");
        assert!(fs::read_to_string(&path).unwrap().contains("after the cap"));
        // The next day starts its own file.
        assert_eq!(write_line(&dir, day(20_761), "warn", "x").unwrap().file_name().unwrap(), "shell-20261104.log");
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn old_and_surplus_files_are_pruned_and_other_files_are_left_alone() {
        let dir = scratch();
        make_dir(&dir).unwrap();
        let now = SystemTime::now();
        for i in 0..20u64 {
            let p = dir.join(format!("shell-2026010{}-{i}.log", i % 10));
            fs::write(&p, "old").unwrap();
            let at = if i < 3 { now - Duration::from_secs(30 * 86_400) } else { now - Duration::from_secs(i * 3600) };
            fs::File::options().write(true).open(&p).unwrap().set_modified(at).unwrap();
        }
        fs::write(dir.join("sidecar-20260101-1.log"), "the sidecar's").unwrap();
        prune(&dir, now, None);
        let left: Vec<_> = fs::read_dir(&dir).unwrap().filter_map(Result::ok).map(|e| e.file_name().into_string().unwrap()).collect();
        assert_eq!(left.iter().filter(|n| n.starts_with("shell-")).count(), MAX_FILES);
        assert!(left.contains(&"sidecar-20260101-1.log".to_string()));
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_panic_leaves_a_scrubbed_crash_record_with_its_location_and_version() {
        let dir = scratch();
        // This thread's folder only: other tests write lines meanwhile, and they must neither land here nor race the cleanup.
        TEST_DIR.with(|d| *d.borrow_mut() = Some(dir.clone()));
        add_secret("panic-secret-77");
        install_panic_hook();
        let caught = std::panic::catch_unwind(|| panic!("boom with panic-secret-77 and password=hunter2"));
        // Back to the default hook, so other tests' panics print as usual.
        let _ = std::panic::take_hook();
        assert!(caught.is_err());
        TEST_DIR.with(|d| *d.borrow_mut() = None);
        let text: String = fs::read_dir(&dir).unwrap().filter_map(Result::ok).map(|e| fs::read_to_string(e.path()).unwrap()).collect();
        let line = text.lines().find(|l| l.contains("[crash] panic at")).expect(&text);
        assert!(line.contains("src/shell_log.rs:") || line.contains("src\\shell_log.rs:"), "{line}");
        assert!(line.contains(&format!("(Querithm {})", env!("CARGO_PKG_VERSION"))), "{line}");
        assert!(line.contains("boom with *** and password=***"), "{line}");
        assert!(!text.contains("hunter2") && !text.contains("panic-secret-77"));
        fs::remove_dir_all(&dir).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn the_log_and_its_folder_are_the_users_alone() {
        use std::os::unix::fs::PermissionsExt;
        let dir = scratch();
        let path = write_line(&dir, day(20_760), "info", "x").unwrap();
        assert_eq!(fs::metadata(&dir).unwrap().permissions().mode() & 0o777, 0o700);
        assert_eq!(fs::metadata(&path).unwrap().permissions().mode() & 0o777, 0o600);
        fs::remove_dir_all(&dir).unwrap();
    }
}
