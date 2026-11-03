// What the IPC relay adds to session.open / profile.test (§5 injection list), as pure functions the tests run without
// Tauri: which vault entries a profile's sign-in needs (Phase D0a) and the file grant of a file profile (Phase D0c).
// The relay (ipc.rs) reads the vault and the grant registry and calls these.
use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use serde_json::{json, Map, Value};

use crate::grants::{GrantError, Registry};

/// What a listed or saved profile needs at open time.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Needs {
    pub db_secret: bool,
    pub ssh_secret: bool,
    /// The grant id of a file profile (`location: "file"`).
    pub file_grant: Option<String>,
}

/// From a profile as the sidecar returns it (or as the editor sends it to profile.test). A missing auth field is a
/// legacy SQL login; the secret rule mirrors ProfileRules.NeedsSecret in the sidecar.
pub fn needs(profile: &Value) -> Needs {
    let db_secret = matches!(profile.get("auth").and_then(Value::as_str).unwrap_or("sql"), "sql" | "password" | "entraToken");
    let ssh_secret = profile.get("ssh").is_some_and(Value::is_object);
    let file_grant = if profile.get("location").and_then(Value::as_str) == Some("file") {
        profile.get("file").and_then(|f| f.get("grantId")).and_then(Value::as_str).map(str::to_owned)
    } else {
        None
    };
    Needs { db_secret, ssh_secret, file_grant }
}

fn known() -> &'static Mutex<HashMap<String, Needs>> {
    static KNOWN: OnceLock<Mutex<HashMap<String, Needs>>> = OnceLock::new();
    KNOWN.get_or_init(|| Mutex::new(HashMap::new()))
}

/// Remembers what a listed or saved profile needs (every profile.list / profile.save reply).
pub fn remember(profile: &Value) {
    if let Some(id) = profile.get("id").and_then(Value::as_str) {
        known().lock().unwrap().insert(id.to_owned(), needs(profile));
    }
}

pub fn forget(profile_id: &str) {
    known().lock().unwrap().remove(profile_id);
}

/// What the open of `params` needs: profile.test from the edited profile it carries, else (and session.open always)
/// from the stored one; a profile not listed yet is looked up as before (secrets yes, no file grant). session.open
/// carries no profile, so one the webview adds never decides what is injected (M1).
pub fn needs_for(method: &str, params: &Value) -> Needs {
    if method == "profile.test" {
        if let Some(p) = params.get("profile").filter(|p| p.is_object()) {
            return needs(p);
        }
    }
    params
        .get("profileId")
        .and_then(Value::as_str)
        .and_then(|id| known().lock().unwrap().get(id).cloned())
        .unwrap_or(Needs { db_secret: true, ssh_secret: true, file_grant: None })
}

/// Names only the shell fills in; the webview never sends one (a null, which the generated contract allows, is dropped).
const SHELL_OWNED: [&str; 2] = ["filePath", "fileGrant"];
/// Names the webview sends itself and the shell reads or fills: the picked file's token, a typed secret.
const SHARED: [&str; 3] = ["fileToken", "secret", "sshSecret"];

/// A case-folded name, close to how .NET's OrdinalIgnoreCase compares (dotless ı and long ſ fold to I and S). Folding
/// more than .NET does only refuses more.
fn fold(name: &str) -> String {
    name.chars().flat_map(char::to_uppercase).collect()
}

fn shell_owned_error(key: &str) -> Value {
    json!({ "code": -32001, "message": format!("The request names \"{key}\", which only the Querithm shell sets."), "data": { "code": "shellOwned" } })
}

fn check_keys(obj: &mut Map<String, Value>) -> Result<(), Value> {
    let mut nulls = Vec::new();
    for (key, value) in obj.iter() {
        let folded = fold(key);
        let Some(name) = SHELL_OWNED.iter().chain(SHARED.iter()).find(|n| fold(n) == folded) else { continue };
        if key != name {
            return Err(shell_owned_error(key));
        }
        if SHELL_OWNED.contains(name) {
            if !value.is_null() {
                return Err(shell_owned_error(key));
            }
            nulls.push(key.clone());
        }
    }
    for key in nulls {
        obj.remove(&key);
    }
    Ok(())
}

/// M1, before anything is injected, on every method: the params object, its `source` / `target` sides and its
/// `profile` must not name a shell-owned key (`filePath`, `fileGrant`) at all, nor any spelling of a shell-owned or
/// shared key (`fileToken`, `secret`, `sshSecret`) other than the exact one. The shell strips and injects by the exact
/// name, and the sidecar once bound names ignoring case, so `FilePath` reached the import past the strip.
pub fn refuse_shell_keys(params: &mut Value) -> Result<(), Value> {
    let Some(top) = params.as_object_mut() else { return Ok(()) };
    check_keys(top)?;
    for nested in ["source", "target", "profile"] {
        if let Some(Value::Object(obj)) = top.get_mut(nested) {
            check_keys(obj)?;
        }
    }
    Ok(())
}

fn grant_error(e: &GrantError) -> Value {
    json!({ "code": -32001, "message": e.message(), "data": { "code": e.code() } })
}

/// The file grant for session.open / profile.test: a `fileGrant` the webview sent is always dropped (only the shell
/// names a path), then the profile's grant is resolved — bound to this profile, still there, same canonical path and
/// identity — and injected. Errors carry a code the UI turns into "Re-pick file…".
pub fn inject_file_grant(params: &mut Value, needs: &Needs, registry: &Registry) -> Result<(), Value> {
    let Some(obj) = params.as_object_mut() else { return Ok(()) };
    obj.remove("fileGrant");
    let Some(id) = needs.file_grant.as_deref() else { return Ok(()) };
    let profile_id = obj.get("profileId").and_then(Value::as_str);
    let injected = registry.resolve(id, profile_id).map_err(|e| grant_error(&e))?;
    obj.insert("fileGrant".into(), serde_json::to_value(injected).expect("serializable"));
    Ok(())
}

/// profile.save may bind only a grant that is unbound or already this profile's: duplicating a profile never carries
/// its grant along. Checked before the call reaches the sidecar.
pub fn check_save(params: &Value, registry: &Registry) -> Result<Option<String>, Value> {
    let n = needs(params);
    let Some(grant) = n.file_grant else { return Ok(None) };
    let id = params.get("id").and_then(Value::as_str).filter(|s| !s.is_empty());
    match registry.check_binding(&grant, id) {
        Ok(()) => Ok(Some(grant)),
        // A new profile (no id yet) may bind an unbound grant only; check_binding(None) allowed that above.
        Err(e) => Err(grant_error(&e)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::grants::Access;
    use std::path::PathBuf;

    fn registry_with_file() -> (Registry, PathBuf) {
        let d = std::env::temp_dir().join(format!("qinject-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()));
        std::fs::create_dir_all(&d).unwrap();
        let f = d.join("sales.db");
        std::fs::write(&f, b"x").unwrap();
        let mut r = Registry::default();
        r.register(&f, "g1".into(), 1).unwrap();
        (r, f)
    }

    fn file_profile(id: &str, grant: &str) -> Value {
        json!({ "id": id, "auth": "none", "location": "file", "file": { "grantId": grant, "displayName": "sales.db" }, "ssh": null })
    }

    #[test]
    fn only_secret_sign_ins_and_tunnels_need_the_vault() {
        assert_eq!(needs(&json!({ "auth": "sql" })), Needs { db_secret: true, ssh_secret: false, file_grant: None });
        assert!(needs(&json!({})).db_secret, "a legacy row is a SQL login");
        assert!(needs(&json!({ "auth": "entraToken" })).db_secret);
        for auth in ["windows", "kerberos", "entraInteractive", "entraDeviceCode", "none"] {
            assert_eq!(needs(&json!({ "auth": auth })), Needs::default(), "{auth}");
        }
        assert!(needs(&json!({ "auth": "none", "ssh": { "host": "b" } })).ssh_secret);
        let unknown = needs_for("session.open", &json!({ "profileId": "never-listed" }));
        assert!(unknown.db_secret && unknown.ssh_secret && unknown.file_grant.is_none());
        remember(&json!({ "id": "local-file", "auth": "none", "ssh": null }));
        assert_eq!(needs_for("session.open", &json!({ "profileId": "local-file" })), Needs::default());
        forget("local-file");
    }

    #[test]
    fn a_file_profile_gets_its_grant_injected_and_a_forged_path_is_dropped() {
        let (mut r, f) = registry_with_file();
        r.bind("g1", "p1").unwrap();
        let mut params = json!({ "profileId": "p1", "fileGrant": { "path": "/etc/shadow", "mode": "readWrite" } });
        inject_file_grant(&mut params, &needs(&file_profile("p1", "g1")), &r).unwrap();
        assert_eq!(params["fileGrant"]["path"], std::fs::canonicalize(&f).unwrap().to_string_lossy().as_ref());
        assert_eq!(params["fileGrant"]["mode"], "readOnly");
        assert_eq!(params["fileGrant"]["grantId"], "g1");
    }

    #[test]
    fn a_server_profile_never_carries_a_file_grant() {
        let (r, _) = registry_with_file();
        let mut params = json!({ "profileId": "p1", "fileGrant": { "path": "/etc/shadow" } });
        inject_file_grant(&mut params, &needs(&json!({ "auth": "sql" })), &r).unwrap();
        assert!(params.get("fileGrant").is_none());
    }

    #[test]
    fn another_profiles_grant_is_refused() {
        let (mut r, _) = registry_with_file();
        r.bind("g1", "p1").unwrap();
        let mut params = json!({ "profileId": "p2" });
        let err = inject_file_grant(&mut params, &needs(&file_profile("p2", "g1")), &r).unwrap_err();
        assert_eq!(err["data"]["code"], "fileGrantBound");
        assert!(params.get("fileGrant").is_none());
    }

    #[test]
    fn a_forged_grant_id_is_refused() {
        let (r, _) = registry_with_file();
        let mut params = json!({ "profileId": "p1" });
        let err = inject_file_grant(&mut params, &needs(&file_profile("p1", "forged")), &r).unwrap_err();
        assert_eq!(err["data"]["code"], "fileGrantUnknown");
    }

    #[test]
    fn a_replaced_file_is_refused_with_the_re_pick_code() {
        let (r, f) = registry_with_file();
        std::fs::remove_file(&f).unwrap();
        std::fs::write(&f, b"another file").unwrap();
        let mut params = json!({ "profile": file_profile("", "g1") });
        let n = needs_for("profile.test", &params);
        let err = inject_file_grant(&mut params, &n, &r).unwrap_err();
        assert_eq!(err["data"]["code"], "fileReplaced");
        assert!(err["message"].as_str().unwrap().contains("Re-pick file"));
    }

    #[test]
    fn saving_may_bind_an_unbound_grant_but_never_another_profiles() {
        let (mut r, _) = registry_with_file();
        assert_eq!(check_save(&file_profile("", "g1"), &r).unwrap().as_deref(), Some("g1"));
        r.bind("g1", "p1").unwrap();
        assert!(check_save(&file_profile("p1", "g1"), &r).is_ok());
        assert_eq!(check_save(&file_profile("p2", "g1"), &r).unwrap_err()["data"]["code"], "fileGrantBound");
        assert_eq!(check_save(&file_profile("", "g1"), &r).unwrap_err()["data"]["code"], "fileGrantBound", "a duplicate cannot take it");
        assert_eq!(check_save(&json!({ "id": "s", "auth": "sql" }), &r).unwrap(), None);
    }

    #[test]
    fn session_open_decides_what_it_needs_from_the_stored_profile_only() {
        remember(&json!({ "id": "srv-m1", "auth": "sql", "ssh": null }));
        // A profile the webview adds to session.open (it has none in the contract) never names the grant to inject.
        let forged = json!({ "profileId": "srv-m1", "profile": file_profile("srv-m1", "g1") });
        assert_eq!(needs_for("session.open", &forged), Needs { db_secret: true, ssh_secret: false, file_grant: None });
        assert_eq!(needs_for("profile.test", &forged).file_grant.as_deref(), Some("g1"), "profile.test tests the edited profile");
        forget("srv-m1");
    }

    fn refused(v: Value) -> bool {
        let mut v = v;
        matches!(refuse_shell_keys(&mut v), Err(e) if e["data"]["code"] == "shellOwned")
    }

    #[test]
    fn a_case_variant_of_a_shell_owned_or_shared_name_is_refused_wherever_the_shell_looks() {
        let variants = ["FilePath", "FILEPATH", "filepath", "FileGrant", "filegrant", "FileToken", "FILETOKEN", "Secret", "SECRET", "SshSecret", "sshsecret", "f\u{131}leToken", "\u{17f}ecret"];
        for key in variants {
            let mut obj = Map::new();
            obj.insert(key.to_owned(), json!("/etc/shadow"));
            let inner = Value::Object(obj);
            assert!(refused(inner.clone()), "top level {key}");
            for side in ["source", "target", "profile"] {
                assert!(refused(json!({ side: inner.clone() })), "{side}.{key}");
            }
        }
    }

    #[test]
    fn the_webview_never_names_a_file_path_or_grant_but_may_send_null_and_its_own_keys() {
        assert!(refused(json!({ "fileToken": "t", "filePath": "/etc/shadow" })));
        assert!(refused(json!({ "profileId": "p", "fileGrant": { "path": "/etc/shadow", "mode": "readWrite" } })));
        assert!(refused(json!({ "source": { "fileToken": "t", "filePath": "/etc/shadow" } })));
        assert!(refused(json!({ "target": { "fileGrant": {} } })));
        assert!(refused(json!({ "profile": { "filePath": "/x" } })));
        let mut ok = json!({ "fileToken": "t", "filePath": null, "secret": "pw", "sshSecret": null, "fileGrant": null, "source": { "fileToken": "u", "filePath": null } });
        refuse_shell_keys(&mut ok).unwrap();
        assert_eq!(ok, json!({ "fileToken": "t", "secret": "pw", "sshSecret": null, "source": { "fileToken": "u" } }), "nulls of shell-owned names are dropped");
        // Deeper data (driver options, grid rows) is the user's and is not looked at.
        let mut deep = json!({ "profile": { "options": { "FilePath": "x" } }, "rows": [{ "Secret": 1 }] });
        refuse_shell_keys(&mut deep).unwrap();
        let mut not_object = json!(["filePath"]);
        refuse_shell_keys(&mut not_object).unwrap();
    }

    #[test]
    fn write_consent_reaches_the_injection() {
        let (mut r, _) = registry_with_file();
        r.allow_writes("g1", 7).unwrap();
        let mut params = json!({ "profile": file_profile("", "g1") });
        let n = needs_for("profile.test", &params);
        inject_file_grant(&mut params, &n, &r).unwrap();
        assert_eq!(params["fileGrant"]["mode"], serde_json::to_value(Access::ReadWrite).unwrap());
    }
}
