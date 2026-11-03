// Where the single-file sidecar extracts its native libraries (SQLite, DuckDB, SNI): DOTNET_BUNDLE_EXTRACT_BASE_DIR.
// H7: `<app local data dir>/native/<build>`, outside every webview fs scope (§13 rule 6: the webview writes only its own
// files under $DATA/querithm). Builds up to 0.2.0 extracted into `<data dir>/querithm/native`, inside the webview's write
// scope, where script in the webview could have replaced a library the next sidecar loads; that folder is removed at
// startup once that is safe (remove_legacy). Pure std: the tests run without Tauri.
//
// R1-2: natives load lazily (DuckDB on its first use), so a folder must not go while an engine of its build still runs,
// however long. Each shell holds a shared lock on its build's marker for its whole life (the OS drops it with the
// process); the sweep takes the marker's lock exclusively before it removes a folder, so it never removes one in use.
use std::fs::{self, File, TryLockError};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Marks a build folder this shell made; a sweep never deletes a folder without it.
const MARKER: &str = ".querithm-native";
/// Every start touches its own build's marker, so a marker this old belongs to a build no Querithm has started for a
/// day. The lock is what keeps a running build's folder; the age is the rule where the file system cannot lock.
const STALE_AFTER: Duration = Duration::from_secs(24 * 60 * 60);
/// R1-2: how long the folder builds up to 0.2.0 extracted into stays after a newer shell first saw it. A 0.2.0 engine
/// still running (two versions open at once) loads DuckDB from it lazily; 0.2.0 takes no lock, so it is given a week.
const LEGACY_GRACE: Duration = Duration::from_secs(7 * 24 * 60 * 60);
/// Beside the build folders: when a newer shell first saw the legacy folder (its modification time).
const LEGACY_SEEN: &str = ".legacy-seen";

/// One folder per sidecar build: the app version, then the sidecar binary's size and modification time (a rebuild of
/// the same version is another build). Only `[0-9A-Za-z.+-]` reach the name.
pub fn build_key(version: &str, sidecar: &Path) -> String {
    let version: String = version.chars().map(|c| if c.is_ascii_alphanumeric() || "+-.".contains(c) { c } else { '_' }).collect();
    match fs::metadata(sidecar) {
        Ok(m) => {
            let mtime = m.modified().ok().and_then(|t| t.duration_since(UNIX_EPOCH).ok()).map(|d| d.as_secs()).unwrap_or(0);
            format!("{version}-{:x}-{mtime:x}", m.len())
        }
        Err(_) => version,
    }
}

/// `<root>/<key>`, created with its marker (touched at every spawn), and a shared lock on the marker that keeps the
/// folder from every sweep while the caller holds it (the shell: for its life). A folder that cannot be created or marked
/// is still returned (.NET creates it, and an unmarked folder is never swept); no lock then.
pub fn prepare(root: &Path, key: &str) -> (PathBuf, Option<File>) {
    let dir = root.join(key);
    if fs::create_dir_all(&dir).is_err() {
        return (dir, None);
    }
    let marker = dir.join(MARKER);
    // Windows refuses writes to a file another handle locked (a second instance of this build): it stays as it is.
    let _ = fs::write(&marker, key);
    let lock = File::open(&marker).ok().filter(|f| f.try_lock_shared().is_ok());
    (dir, lock)
}

/// Whether the build folder of `marker` may go: no shell holds its lock (Ok(true) after taking it exclusively), one
/// does (false), or the file system cannot lock (the age rule alone decides).
fn unused(marker: &Path) -> Option<(bool, Option<File>)> {
    let f = File::open(marker).ok()?;
    Some(match f.try_lock() {
        Ok(()) => (true, Some(f)),
        Err(TryLockError::WouldBlock) => (false, None),
        Err(TryLockError::Error(_)) => (true, None),
    })
}

/// Removes the extraction folders of other builds: real directories directly under `root` (a symlink is never
/// followed or removed) that carry this shell's marker, untouched for [`STALE_AFTER`]. Returns how many went.
pub fn sweep(root: &Path, keep: &str, now: SystemTime) -> usize {
    let Ok(entries) = fs::read_dir(root) else { return 0 };
    let mut removed = 0;
    for entry in entries.flatten() {
        if entry.file_name() == keep {
            continue;
        }
        // A DirEntry's file type is the entry's own: a symlink to a directory is a symlink, not a directory.
        if !entry.file_type().is_ok_and(|t| t.is_dir()) {
            continue;
        }
        let path = entry.path();
        let Ok(marker) = fs::symlink_metadata(path.join(MARKER)) else { continue };
        let stale = marker.file_type().is_file() && marker.modified().ok().and_then(|m| now.duration_since(m).ok()).is_some_and(|age| age >= STALE_AFTER);
        if !stale {
            continue;
        }
        // A shell still running that build holds the marker's lock: its engine may load a library from it any time.
        let Some((free, held)) = unused(&path.join(MARKER)) else { continue };
        if !free {
            continue;
        }
        // Windows: the marker cannot go while this handle is open.
        drop(held);
        // remove_dir_all removes links inside the tree without following them.
        if fs::remove_dir_all(&path).is_ok() {
            removed += 1;
        }
    }
    removed
}

/// The folder builds up to 0.2.0 extracted into (`<data dir>/querithm/native`, inside the webview's write scope). R1-2:
/// a 0.2.0 engine may still run from it (it loads SQLite at start and DuckDB lazily, and takes no lock), so it goes only
/// [`LEGACY_GRACE`] after a newer shell first saw it (`seen`, a file this shell keeps outside every webview scope), and
/// only when it can first be renamed away (Windows refuses while a library in it is loaded: a 0.2.0 engine runs). A
/// symlink in its place is left alone (never followed); nothing newer reads that path.
pub fn remove_legacy(dir: &Path, seen: &Path, now: SystemTime) -> bool {
    if !matches!(fs::symlink_metadata(dir), Ok(m) if m.file_type().is_dir()) {
        let _ = fs::remove_file(seen);
        return false;
    }
    let first = fs::metadata(seen).and_then(|m| m.modified());
    let Ok(first) = first else {
        if let Some(parent) = seen.parent() {
            let _ = fs::create_dir_all(parent);
        }
        let _ = fs::write(seen, b"");
        return false;
    };
    if now.duration_since(first).map_or(true, |age| age < LEGACY_GRACE) {
        return false;
    }
    let aside = dir.with_file_name("native.removing");
    let _ = fs::remove_dir_all(&aside); // a removal an earlier start could not finish
    if fs::rename(dir, &aside).is_err() {
        return false;
    }
    let _ = fs::remove_file(seen);
    fs::remove_dir_all(&aside).is_ok()
}

/// The `seen` file of [`remove_legacy`] under `root` (the build folders' root).
pub fn legacy_seen(root: &Path) -> PathBuf {
    root.join(LEGACY_SEEN)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(name: &str) -> PathBuf {
        let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let d = std::env::temp_dir().join(format!("qnative-{name}-{}-{nanos}", std::process::id()));
        fs::create_dir_all(&d).unwrap();
        d
    }

    fn later() -> SystemTime {
        SystemTime::now() + STALE_AFTER + Duration::from_secs(60)
    }

    #[test]
    fn a_build_gets_its_own_marked_folder_and_the_key_names_the_binary() {
        let root = temp("prepare");
        let bin = root.join("querithm-sidecar");
        fs::write(&bin, b"12345").unwrap();
        let key = build_key("0.2.1", &bin);
        assert!(key.starts_with("0.2.1-5-"), "{key}");
        assert_eq!(build_key("0.2.1", &root.join("missing")), "0.2.1");
        assert_eq!(build_key("1.0/../x", &root.join("missing")), "1.0_.._x", "never a path separator");
        let (dir, lock) = prepare(&root.join("native"), &key);
        assert_eq!(dir, root.join("native").join(&key));
        assert!(dir.join(MARKER).is_file());
        assert!(lock.is_some(), "the marker is locked for the shell's life");
        // A second instance of the same build shares the lock.
        let (_, second) = prepare(&root.join("native"), &key);
        assert!(second.is_some());
        drop((lock, second));
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn a_folder_whose_marker_a_running_shell_holds_is_never_swept_however_old() {
        let root = temp("held");
        let (old, lock) = prepare(&root, "0.2.0-c-d");
        fs::write(old.join("libduckdb.so"), b"x").unwrap();
        assert_eq!(sweep(&root, "0.2.1-a-b", later()), 0, "another build's engine still runs from it");
        assert!(old.join("libduckdb.so").exists());
        drop(lock); // that shell exits
        assert_eq!(sweep(&root, "0.2.1-a-b", later()), 1);
        assert!(!old.exists());
        fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn the_sweep_removes_stale_marked_folders_of_other_builds_only() {
        let root = temp("sweep");
        let (current, _held) = prepare(&root, "0.2.1-a-b");
        let (old, _) = prepare(&root, "0.2.0-c-d");
        fs::write(old.join("libduckdb.so"), b"x").unwrap();
        let foreign = root.join("not-ours");
        fs::create_dir_all(&foreign).unwrap();
        fs::write(root.join("a-file"), b"x").unwrap();
        // Recently started: another Querithm may still run that build.
        assert_eq!(sweep(&root, "0.2.1-a-b", SystemTime::now()), 0);
        assert!(old.exists());
        assert_eq!(sweep(&root, "0.2.1-a-b", later()), 1);
        assert!(!old.exists());
        assert!(current.join(MARKER).is_file(), "the running build's folder stays, however old its marker");
        assert!(foreign.exists(), "a folder without the marker was not made by the shell");
        assert!(root.join("a-file").exists());
        fs::remove_dir_all(&root).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn the_sweep_never_follows_a_symlink() {
        let root = temp("links");
        let outside = temp("outside");
        fs::write(outside.join(MARKER), b"x").unwrap();
        fs::write(outside.join("keep.txt"), b"x").unwrap();
        // A link standing for a build folder, and a link inside a stale build folder.
        std::os::unix::fs::symlink(&outside, root.join("0.1.0-linked")).unwrap();
        let (old, _) = prepare(&root, "0.2.0-c-d");
        std::os::unix::fs::symlink(&outside, old.join("inner")).unwrap();
        assert_eq!(sweep(&root, "0.2.1-a-b", later()), 1);
        assert!(!old.exists());
        assert!(root.join("0.1.0-linked").symlink_metadata().is_ok(), "the link itself stays");
        assert!(outside.join("keep.txt").exists(), "nothing behind a link was removed");
        fs::remove_dir_all(&root).unwrap();
        fs::remove_dir_all(&outside).unwrap();
    }

    #[test]
    fn the_legacy_folder_inside_the_webview_scope_goes_a_week_after_it_was_first_seen() {
        let data = temp("legacy");
        let legacy = data.join("native");
        let seen = legacy_seen(&data.join("local-native"));
        fs::create_dir_all(legacy.join("querithm-sidecar").join("abc")).unwrap();
        fs::write(legacy.join("querithm-sidecar").join("abc").join("libe_sqlite3.so"), b"x").unwrap();
        // First seen now: a 0.2.0 engine may still run from it.
        assert!(!remove_legacy(&legacy, &seen, SystemTime::now()));
        assert!(legacy.exists() && seen.is_file());
        assert!(!remove_legacy(&legacy, &seen, SystemTime::now() + LEGACY_GRACE - Duration::from_secs(60)));
        assert!(legacy.exists());
        assert!(remove_legacy(&legacy, &seen, SystemTime::now() + LEGACY_GRACE + Duration::from_secs(60)));
        assert!(!legacy.exists() && !data.join("native.removing").exists());
        assert!(!seen.exists(), "nothing is kept once it is gone");
        assert!(!remove_legacy(&legacy, &seen, SystemTime::now()), "nothing to remove the next time");
        assert!(!seen.exists());
        #[cfg(unix)]
        {
            let outside = temp("legacy-target");
            fs::write(outside.join("keep.txt"), b"x").unwrap();
            std::os::unix::fs::symlink(&outside, &legacy).unwrap();
            assert!(!remove_legacy(&legacy, &seen, SystemTime::now() + LEGACY_GRACE * 2));
            assert!(outside.join("keep.txt").exists());
            fs::remove_dir_all(&outside).unwrap();
        }
        fs::remove_dir_all(&data).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn a_legacy_folder_that_cannot_be_renamed_away_is_left_whole() {
        use std::os::unix::fs::PermissionsExt;
        let data = temp("legacy-busy");
        let legacy = data.join("native");
        let seen = legacy_seen(&data.join("local-native"));
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("libduckdb.so"), b"x").unwrap();
        assert!(!remove_legacy(&legacy, &seen, SystemTime::now()));
        // The rename fails (as Windows refuses one while a loaded library is in the folder): nothing in it is deleted.
        fs::set_permissions(&data, fs::Permissions::from_mode(0o500)).unwrap();
        let removed = remove_legacy(&legacy, &seen, SystemTime::now() + LEGACY_GRACE * 2);
        fs::set_permissions(&data, fs::Permissions::from_mode(0o700)).unwrap();
        assert!(!removed);
        assert!(legacy.join("libduckdb.so").exists());
        fs::remove_dir_all(&data).unwrap();
    }
}
