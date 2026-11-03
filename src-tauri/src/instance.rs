// Which app instance owns the saved workspace (M23). Several instances stay allowed (owner decision), but the tabs in
// `scripts/workspace.json` belong to one of them: the first holds an exclusive lock on `<data dir>/querithm/instance.lock`
// (outside every webview scope) for as long as it runs; the OS drops it when the process ends, a crash included. A later
// instance starts with a new tab and neither restores nor rewrites the workspace, so two windows never edit the same
// tab's script file or overwrite each other's tab list. A lock file that cannot be opened at all keeps the old behaviour
// (this instance owns the workspace): only a lock another process holds makes one a second window. R1-7: a lock held is
// tried again a few times first — Windows releases a crashed process's lock asynchronously, and a restart (an update,
// a relaunch after a crash) can start the new process before the old one is gone.
use std::fs::{File, OpenOptions, TryLockError};
use std::path::Path;
use std::time::Duration;

/// How many more times a held lock is tried, and how far apart: at most 600 ms more for a real second window.
const CLAIM_RETRIES: u32 = 3;
const CLAIM_RETRY_EVERY: Duration = Duration::from_millis(200);

pub struct Workspace {
    _lock: Option<File>,
    pub owner: bool,
}

impl Workspace {
    /// No data folder to lock in: this instance owns the workspace, as before M23.
    pub const UNCLAIMED: Workspace = Workspace { _lock: None, owner: true };
}

pub fn claim(data_dir: &Path) -> Workspace {
    claim_with(data_dir, CLAIM_RETRIES, CLAIM_RETRY_EVERY)
}

fn claim_with(data_dir: &Path, retries: u32, every: Duration) -> Workspace {
    let opened = (|| {
        let mut b = std::fs::DirBuilder::new();
        b.recursive(true);
        #[cfg(unix)]
        std::os::unix::fs::DirBuilderExt::mode(&mut b, 0o700);
        b.create(data_dir)?;
        let mut o = OpenOptions::new();
        o.create(true).write(true).truncate(false);
        #[cfg(unix)]
        std::os::unix::fs::OpenOptionsExt::mode(&mut o, 0o600);
        o.open(data_dir.join("instance.lock"))
    })();
    let Ok(f) = opened else { return Workspace { _lock: None, owner: true } };
    let mut attempt = 0;
    loop {
        match f.try_lock() {
            Ok(()) => return Workspace { _lock: Some(f), owner: true },
            Err(TryLockError::WouldBlock) if attempt < retries => {
                attempt += 1;
                std::thread::sleep(every);
            }
            Err(TryLockError::WouldBlock) => return Workspace { _lock: None, owner: false },
            Err(TryLockError::Error(_)) => return Workspace { _lock: None, owner: true },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_first_instance_owns_the_workspace_until_it_ends() {
        let dir = std::env::temp_dir().join(format!("qinstance-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let first = claim(&dir);
        assert!(first.owner);
        let second = claim_with(&dir, 0, Duration::ZERO);
        assert!(!second.owner, "a second instance while the first runs");
        drop(second);
        drop(first);
        assert!(claim(&dir).owner, "the lock went with the first instance");
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_lock_released_shortly_after_start_still_makes_this_instance_the_owner() {
        // R1-7: the previous instance (a crash Windows has not cleaned up yet, a restart) lets go ~250 ms after this start.
        let dir = std::env::temp_dir().join(format!("qinstance-late-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let previous = claim(&dir);
        assert!(previous.owner);
        let releaser = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(250));
            drop(previous);
        });
        let started = std::time::Instant::now();
        let next = claim(&dir);
        releaser.join().unwrap();
        assert!(next.owner, "a lock released within the retries is taken");
        assert!(started.elapsed() < Duration::from_secs(5));
        // A lock that stays held: a real second window, after at most the retries.
        let started = std::time::Instant::now();
        let second = claim(&dir);
        assert!(!second.owner);
        assert!(started.elapsed() >= CLAIM_RETRY_EVERY * CLAIM_RETRIES);
        drop((next, second));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_lock_file_that_cannot_be_made_keeps_the_instance_the_owner() {
        let blocker = std::env::temp_dir().join(format!("qinstance-file-{}", std::process::id()));
        std::fs::write(&blocker, "a file where the folder should be").unwrap();
        assert!(claim(&blocker.join("sub")).owner);
        std::fs::remove_file(&blocker).unwrap();
    }
}
