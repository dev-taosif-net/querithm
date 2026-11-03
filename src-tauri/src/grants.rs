// Database-file grants (Phase D0c). A file database is opened only through a grant: the shell's own dialog picked
// the file, the shell keeps {canonical path, file identity, access mode, profile, consent} here, and the webview only
// ever sees an opaque id and the file's name. The registry lives in the shell's config directory, outside every
// webview fs scope (capabilities/default.json scopes $DATA/querithm/** only), so the webview can neither forge nor
// alter an entry. Read-only by default; writes need the consent dialog the shell shows itself (files.rs).
//
// Pure std + serde: no window, no dialog, no driver — the tests run without Tauri.
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Access {
    ReadOnly,
    ReadWrite,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Grant {
    /// Canonical absolute path (symlinks resolved) as picked.
    pub path: PathBuf,
    /// The file's identity when it was picked: "u:<dev>:<ino>" (Unix) or "w:<volume serial>:<file index>" (Windows).
    pub identity: String,
    pub access: Access,
    /// The profile the grant belongs to; the first save binds it, and it never moves to another profile.
    pub profile_id: Option<String>,
    /// Seconds since the epoch when the user allowed writes in the shell's dialog; None = read-only.
    pub consent_at: Option<u64>,
    pub created_at: u64,
}

/// What the relay injects into session.open / profile.test (the sidecar re-checks the identity after opening).
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Injected {
    pub grant_id: String,
    pub path: String,
    pub mode: Access,
    pub identity: String,
}

/// Why a grant cannot be used; every case asks the user to pick the file again (never silently another file).
#[derive(Clone, Debug, PartialEq)]
pub enum GrantError {
    Unknown,
    BoundToOtherProfile,
    Missing,
    Moved,
    Replaced,
}

impl GrantError {
    pub fn code(&self) -> &'static str {
        match self {
            GrantError::Unknown => "fileGrantUnknown",
            GrantError::BoundToOtherProfile => "fileGrantBound",
            GrantError::Missing => "fileMissing",
            GrantError::Moved => "fileMoved",
            GrantError::Replaced => "fileReplaced",
        }
    }

    pub fn message(&self) -> &'static str {
        match self {
            GrantError::Unknown => "This connection has no access to its database file any more. Pick the file again (Re-pick file…).",
            GrantError::BoundToOtherProfile => "That file was picked for another connection. Pick it again for this one (Re-pick file…).",
            GrantError::Missing => "The database file is gone (deleted, moved or on a drive that is not connected). Pick it again (Re-pick file…).",
            GrantError::Moved => "The database file is no longer at the path it was picked from. Pick it again (Re-pick file…).",
            GrantError::Replaced => "The file at that path is not the one that was picked (it was replaced). Pick it again (Re-pick file…).",
        }
    }
}

#[derive(Default, Debug, PartialEq, Serialize, Deserialize)]
pub struct Registry {
    version: u32,
    grants: HashMap<String, Grant>,
}

pub const VERSION: u32 = 1;

impl Registry {
    /// The registry at `file`; a missing file is empty, and so is an unreadable or foreign one (fail closed: no grants).
    pub fn load(file: &Path) -> Registry {
        let Ok(text) = std::fs::read_to_string(file) else { return Registry { version: VERSION, ..Default::default() } };
        match serde_json::from_str::<Registry>(&text) {
            Ok(r) if r.version == VERSION => r,
            _ => Registry { version: VERSION, ..Default::default() },
        }
    }

    /// Written to a temporary file and renamed over the old one, so a crash never leaves half a registry. Unix: 0600.
    pub fn save(&self, file: &Path) -> io::Result<()> {
        if let Some(dir) = file.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = file.with_extension("json.tmp");
        let text = serde_json::to_string_pretty(&Registry { version: VERSION, grants: self.grants.clone() }).map_err(io::Error::other)?;
        write_private(&tmp, text.as_bytes())?;
        std::fs::rename(&tmp, file)
    }

    pub fn get(&self, id: &str) -> Option<&Grant> {
        self.grants.get(id)
    }

    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.grants.len()
    }

    /// A newly picked file: canonical path and identity, read-only. Returns the new id and the file's display name.
    pub fn register(&mut self, picked: &Path, id: String, now: u64) -> io::Result<(String, String)> {
        let path = std::fs::canonicalize(picked)?;
        let meta = std::fs::metadata(&path)?;
        if !meta.is_file() {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "Pick a database file, not a folder."));
        }
        let identity = identity_of(&path)?;
        let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        self.grants.insert(id.clone(), Grant { path, identity, access: Access::ReadOnly, profile_id: None, consent_at: None, created_at: now });
        Ok((id, name))
    }

    /// Called only after the user answered "Allow writes" in the shell's own dialog; the webview cannot reach this.
    pub fn allow_writes(&mut self, id: &str, now: u64) -> Result<(), GrantError> {
        let g = self.grants.get_mut(id).ok_or(GrantError::Unknown)?;
        g.access = Access::ReadWrite;
        g.consent_at = Some(now);
        Ok(())
    }

    /// Back to read-only (the user withdrew write access).
    pub fn read_only(&mut self, id: &str) -> Result<(), GrantError> {
        let g = self.grants.get_mut(id).ok_or(GrantError::Unknown)?;
        g.access = Access::ReadOnly;
        g.consent_at = None;
        Ok(())
    }

    /// May `profile_id` use grant `id`? Unbound grants may be used by anyone until a save binds them.
    pub fn check_binding(&self, id: &str, profile_id: Option<&str>) -> Result<(), GrantError> {
        let g = self.grants.get(id).ok_or(GrantError::Unknown)?;
        match (&g.profile_id, profile_id) {
            (None, _) => Ok(()),
            (Some(owner), Some(pid)) if owner == pid => Ok(()),
            _ => Err(GrantError::BoundToOtherProfile),
        }
    }

    /// Binds an unbound grant to the profile that saved it; a grant bound to another profile is refused, so duplicating
    /// a profile never carries its (possibly writable) grant along.
    pub fn bind(&mut self, id: &str, profile_id: &str) -> Result<bool, GrantError> {
        self.check_binding(id, Some(profile_id))?;
        let g = self.grants.get_mut(id).ok_or(GrantError::Unknown)?;
        let changed = g.profile_id.is_none();
        g.profile_id = Some(profile_id.to_owned());
        Ok(changed)
    }

    /// The grant ready for injection: bound to this profile (or unbound), the file still there, at the same canonical
    /// path, with the same identity. Format and corruption are the driver's to judge, in the sidecar.
    pub fn resolve(&self, id: &str, profile_id: Option<&str>) -> Result<Injected, GrantError> {
        self.check_binding(id, profile_id)?;
        let g = &self.grants[id];
        let meta = std::fs::metadata(&g.path).map_err(|_| GrantError::Missing)?;
        if !meta.is_file() {
            return Err(GrantError::Missing);
        }
        let canonical = std::fs::canonicalize(&g.path).map_err(|_| GrantError::Missing)?;
        if canonical != g.path {
            return Err(GrantError::Moved); // a symlink now sits on the path, or a component moved
        }
        let identity = identity_of(&g.path).map_err(|_| GrantError::Missing)?;
        if identity != g.identity {
            return Err(GrantError::Replaced);
        }
        Ok(Injected { grant_id: id.to_owned(), path: g.path.to_string_lossy().into_owned(), mode: g.access, identity })
    }

    #[cfg(test)]
    pub fn revoke(&mut self, id: &str) -> bool {
        self.grants.remove(id).is_some()
    }

    /// Every grant of a deleted (or revoked) profile.
    pub fn remove_profile(&mut self, profile_id: &str) -> usize {
        let before = self.grants.len();
        self.grants.retain(|_, g| g.profile_id.as_deref() != Some(profile_id));
        before - self.grants.len()
    }
}

#[cfg(unix)]
fn write_private(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    use std::os::unix::fs::OpenOptionsExt;
    let mut f = std::fs::OpenOptions::new().write(true).create(true).truncate(true).mode(0o600).open(path)?;
    f.write_all(bytes)?;
    f.sync_all()
}

#[cfg(not(unix))]
fn write_private(path: &Path, bytes: &[u8]) -> io::Result<()> {
    std::fs::write(path, bytes)
}

/// The file's identity: device and inode (Unix), volume serial and file index (Windows). Survives writes and renames
/// of other files; changes when the path is replaced by another file. The sidecar computes the same string.
#[cfg(unix)]
pub fn identity_of(path: &Path) -> io::Result<String> {
    use std::os::unix::fs::MetadataExt;
    let m = std::fs::metadata(path)?;
    Ok(format!("u:{}:{}", m.dev(), m.ino()))
}

#[cfg(windows)]
pub fn identity_of(path: &Path) -> io::Result<String> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{GetFileInformationByHandle, BY_HANDLE_FILE_INFORMATION, FILE_READ_ATTRIBUTES};
    // Attributes only: sharing is checked for data access, so this handle is granted even while the sidecar's engine
    // holds the file without sharing (DuckDB opens a writable database so); a read handle was refused there.
    let f = std::fs::OpenOptions::new().access_mode(FILE_READ_ATTRIBUTES).open(path)?;
    let mut info: BY_HANDLE_FILE_INFORMATION = unsafe { std::mem::zeroed() };
    // SAFETY: a valid handle of an open file and a zeroed out-structure of the documented type.
    let ok = unsafe { GetFileInformationByHandle(f.as_raw_handle() as _, &mut info) };
    if ok == 0 {
        return Err(io::Error::last_os_error());
    }
    let index = ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64;
    Ok(format!("w:{}:{}", info.dwVolumeSerialNumber, index))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dir() -> PathBuf {
        let d = std::env::temp_dir().join(format!("qgrants-{}-{}", std::process::id(), rand_id()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn rand_id() -> u64 {
        use std::time::{SystemTime, UNIX_EPOCH};
        static N: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos() as u64 ^ N.fetch_add(1, std::sync::atomic::Ordering::SeqCst)
    }

    fn file(d: &Path, name: &str) -> PathBuf {
        let p = d.join(name);
        std::fs::write(&p, b"SQLite format 3\0").unwrap();
        p
    }

    #[test]
    fn a_picked_file_is_read_only_and_resolves_with_its_canonical_path_and_identity() {
        let d = dir();
        let f = file(&d, "orders.db");
        let mut r = Registry::default();
        let (id, name) = r.register(&f, "g1".into(), 10).unwrap();
        assert_eq!((id.as_str(), name.as_str()), ("g1", "orders.db"));
        let g = r.get("g1").unwrap();
        assert_eq!(g.access, Access::ReadOnly);
        assert_eq!(g.path, std::fs::canonicalize(&f).unwrap());
        let inj = r.resolve("g1", None).unwrap();
        assert_eq!(inj.mode, Access::ReadOnly);
        assert_eq!(inj.identity, identity_of(&f).unwrap());
    }

    /// Regression: the identity was read through a handle asking for read access, which DuckDB's unshared open of a
    /// writable database refuses on Windows: a second tab on that file was told the file was gone.
    #[cfg(windows)]
    #[test]
    fn a_grant_resolves_while_an_engine_holds_its_file_open_exclusively() {
        use std::os::windows::fs::OpenOptionsExt;
        let d = dir();
        let f = file(&d, "orders.db");
        let mut r = Registry::default();
        r.register(&f, "g1".into(), 10).unwrap();
        let _engine = std::fs::OpenOptions::new().read(true).write(true).share_mode(0).open(&f).unwrap();
        assert_eq!(r.resolve("g1", None).unwrap().identity, r.get("g1").unwrap().identity);
    }

    #[test]
    fn unknown_ids_are_refused() {
        let r = Registry::default();
        assert_eq!(r.resolve("forged", Some("p1")), Err(GrantError::Unknown));
    }

    #[test]
    fn a_grant_belongs_to_the_profile_that_saved_it() {
        let d = dir();
        let mut r = Registry::default();
        r.register(&file(&d, "a.db"), "g".into(), 1).unwrap();
        assert!(r.bind("g", "p1").unwrap());
        assert!(!r.bind("g", "p1").unwrap());
        assert_eq!(r.bind("g", "p2"), Err(GrantError::BoundToOtherProfile));
        assert_eq!(r.resolve("g", Some("p2")), Err(GrantError::BoundToOtherProfile));
        assert_eq!(r.resolve("g", None), Err(GrantError::BoundToOtherProfile), "an unsaved copy cannot borrow it either");
        assert!(r.resolve("g", Some("p1")).is_ok());
    }

    #[test]
    fn writes_need_the_recorded_consent_and_can_be_withdrawn() {
        let d = dir();
        let mut r = Registry::default();
        r.register(&file(&d, "a.db"), "g".into(), 1).unwrap();
        r.allow_writes("g", 99).unwrap();
        assert_eq!((r.get("g").unwrap().access, r.get("g").unwrap().consent_at), (Access::ReadWrite, Some(99)));
        assert_eq!(r.resolve("g", None).unwrap().mode, Access::ReadWrite);
        r.read_only("g").unwrap();
        assert_eq!(r.resolve("g", None).unwrap().mode, Access::ReadOnly);
        assert_eq!(r.allow_writes("nope", 1), Err(GrantError::Unknown));
    }

    #[test]
    fn a_missing_moved_or_replaced_file_is_refused_never_opened_instead() {
        let d = dir();
        let f = file(&d, "a.db");
        let mut r = Registry::default();
        r.register(&f, "g".into(), 1).unwrap();
        // Replaced: same path, another file (a new inode).
        let other = file(&d, "other.db");
        std::fs::rename(&other, &f).unwrap();
        assert_eq!(r.resolve("g", None), Err(GrantError::Replaced));
        // Missing.
        std::fs::remove_file(&f).unwrap();
        assert_eq!(r.resolve("g", None), Err(GrantError::Missing));
    }

    #[cfg(unix)]
    #[test]
    fn a_symlink_planted_on_the_path_is_moved_not_followed() {
        let d = dir();
        let f = file(&d, "a.db");
        let mut r = Registry::default();
        r.register(&f, "g".into(), 1).unwrap();
        let real = d.join("real.db");
        std::fs::rename(&f, &real).unwrap();
        std::os::unix::fs::symlink(&real, &f).unwrap();
        assert_eq!(r.resolve("g", None), Err(GrantError::Moved));
    }

    #[cfg(unix)]
    #[test]
    fn picking_through_a_symlink_or_dot_dot_records_the_real_path() {
        let d = dir();
        let real = file(&d, "real.db");
        let link = d.join("link.db");
        std::os::unix::fs::symlink(&real, &link).unwrap();
        let mut r = Registry::default();
        r.register(&link, "g1".into(), 1).unwrap();
        std::fs::create_dir_all(d.join("sub")).unwrap();
        r.register(&d.join("sub").join("..").join("real.db"), "g2".into(), 1).unwrap();
        let canonical = std::fs::canonicalize(&real).unwrap();
        assert_eq!(r.get("g1").unwrap().path, canonical);
        assert_eq!(r.get("g2").unwrap().path, canonical);
    }

    #[test]
    fn folders_cannot_be_granted() {
        let d = dir();
        let mut r = Registry::default();
        assert!(r.register(&d, "g".into(), 1).is_err());
        assert_eq!(r.len(), 0);
    }

    #[test]
    fn the_registry_survives_a_restart_and_a_foreign_or_corrupt_file_grants_nothing() {
        let d = dir();
        let store = d.join("cfg").join("grants.json");
        let mut r = Registry::load(&store);
        r.register(&file(&d, "a.db"), "g".into(), 1).unwrap();
        r.bind("g", "p1").unwrap();
        r.allow_writes("g", 5).unwrap();
        r.save(&store).unwrap();
        let back = Registry::load(&store);
        assert_eq!(back.get("g"), r.get("g"));
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            assert_eq!(std::fs::metadata(&store).unwrap().permissions().mode() & 0o777, 0o600);
        }
        std::fs::write(&store, b"{ not json").unwrap();
        assert_eq!(Registry::load(&store).len(), 0);
        std::fs::write(&store, br#"{"version":99,"grants":{}}"#).unwrap();
        assert_eq!(Registry::load(&store).len(), 0);
    }

    #[test]
    fn deleting_a_profile_removes_its_grants_only() {
        let d = dir();
        let mut r = Registry::default();
        r.register(&file(&d, "a.db"), "a".into(), 1).unwrap();
        r.register(&file(&d, "b.db"), "b".into(), 1).unwrap();
        r.bind("a", "p1").unwrap();
        r.bind("b", "p2").unwrap();
        assert_eq!(r.remove_profile("p1"), 1);
        assert!(r.get("a").is_none() && r.get("b").is_some());
        assert!(r.revoke("b"));
        assert!(!r.revoke("b"));
    }
}
