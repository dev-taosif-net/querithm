// User-picked files for the import wizard. The webview never names a path: the native dialog returns a
// path here, the webview gets an opaque token, and the IPC relay swaps the token for the path when it forwards
// import.* calls to the sidecar. A token works until an import from it has started (one-time; a Production guard
// block does not spend it), and paths sent by the webview are stripped. This keeps §13 rule 6 (file access limited to user-picked paths) true for the sidecar too.
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Mutex;

use serde::Serialize;
use serde_json::{json, Value};
use tauri::{AppHandle, State};

struct Picked {
    path: PathBuf,
    used: bool,
    running: bool,
}

#[derive(Default)]
pub struct PickedFiles {
    map: Mutex<HashMap<String, Picked>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PickedFile {
    token: String,
    name: String,
    size: u64,
}

fn new_token() -> String {
    let mut bytes = [0u8; 16];
    getrandom::getrandom(&mut bytes).expect("os random");
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

impl PickedFiles {
    pub fn register(&self, path: PathBuf) -> String {
        let token = new_token();
        self.map.lock().unwrap().insert(token.clone(), Picked { path, used: false, running: false });
        token
    }

    /// The path of a live token. `run` (import.run) holds the token until [`PickedFiles::settle_run`]: a second
    /// import.run is refused meanwhile, and only a run that started an import spends the token.
    pub fn resolve(&self, token: &str, run: bool) -> Option<PathBuf> {
        let mut map = self.map.lock().unwrap();
        let p = map.get_mut(token)?;
        if p.used || (run && p.running) {
            return None;
        }
        if run {
            p.running = true;
        }
        Some(p.path.clone())
    }

    /// Ends the import.run holding the token: `started` (the reply carried a jobId) makes it one-time; a guard block
    /// or a failed call leaves it usable for the retry.
    pub fn settle_run(&self, token: &str, started: bool) {
        if let Some(p) = self.map.lock().unwrap().get_mut(token) {
            p.running = false;
            if started {
                p.used = true;
            }
        }
    }
}

/// For import.inspect / import.run (and the file sides of schemaCompare.*): drop any filePath the webview sent and
/// inject the one its fileToken stands for. A call without a token that resolves is refused, never passed on without a
/// path (M1). For import.run it returns the token, which the caller settles with [`PickedFiles::settle_run`] once the
/// sidecar replied.
pub fn inject(method: &str, params: &mut Value, files: &PickedFiles) -> Result<Option<String>, Value> {
    if method == "import.inspect" || method == "import.run" {
        let run = method == "import.run";
        let Some(obj) = params.as_object_mut() else { return Err(no_token()) };
        let token = inject_one(obj, files, run)?;
        return Ok(run.then_some(token));
    }
    if method.starts_with("schemaCompare.") {
        if let Some(obj) = params.as_object_mut() {
            for side in ["source", "target"] {
                let Some(Value::Object(v)) = obj.get_mut(side) else { continue };
                v.remove("filePath");
                // A side that names a file (a fileToken) gets its path; a live side (a session) names none.
                if v.get("fileToken").is_some_and(|t| !t.is_null()) {
                    inject_one(v, files, false)?;
                }
            }
        }
    }
    Ok(None)
}

/// Whether an import.run reply started an import (a guard block has no jobId).
pub fn run_started(reply: &Result<Value, Value>) -> bool {
    matches!(reply, Ok(v) if v.get("jobId").is_some_and(|j| !j.is_null()))
}

fn no_token() -> Value {
    json!({ "code": -32001, "message": "No file was picked for this call. Pick the file with the file dialog.", "data": { "code": "fileToken" } })
}

fn inject_one(obj: &mut serde_json::Map<String, Value>, files: &PickedFiles, run: bool) -> Result<String, Value> {
    obj.remove("filePath");
    let Some(Value::String(token)) = obj.get("fileToken").cloned() else { return Err(no_token()) };
    match files.resolve(&token, run) {
        Some(path) => {
            obj.insert("filePath".into(), Value::String(path.to_string_lossy().into_owned()));
            Ok(token)
        }
        None => Err(json!({ "code": -32001, "message": "The picked file is no longer available. Pick it again.", "data": { "code": "fileToken" } })),
    }
}

/// Native open dialog for data files; returns a token, never the path.
#[tauri::command]
pub async fn import_pick_file(app: AppHandle, files: State<'_, PickedFiles>) -> Result<Option<PickedFile>, String> {
    use tauri_plugin_dialog::DialogExt;
    let (tx, rx) = tokio::sync::oneshot::channel();
    app.dialog()
        .file()
        .add_filter("Data files", &["csv", "tsv", "txt", "json", "ndjson", "jsonl", "xlsx"])
        .pick_file(move |p| {
            let _ = tx.send(p);
        });
    let Some(picked) = rx.await.map_err(|e| e.to_string())? else { return Ok(None) };
    let path = picked.into_path().map_err(|e| e.to_string())?;
    let size = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    Ok(Some(PickedFile { token: files.register(path), name, size }))
}

/// Native open dialog for saved schema models; returns a token, never the path.
#[tauri::command]
pub async fn compare_pick_file(app: AppHandle, files: State<'_, PickedFiles>) -> Result<Option<PickedFile>, String> {
    use tauri_plugin_dialog::DialogExt;
    let (tx, rx) = tokio::sync::oneshot::channel();
    app.dialog()
        .file()
        .add_filter("Querithm schema", &["json"])
        .pick_file(move |p| {
            let _ = tx.send(p);
        });
    let Some(picked) = rx.await.map_err(|e| e.to_string())? else { return Ok(None) };
    let path = picked.into_path().map_err(|e| e.to_string())?;
    let size = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    Ok(Some(PickedFile { token: files.register(path), name, size }))
}

// ---- database-file grants (Phase D0c) ----

/// The durable grant registry (grants.rs) at `<app config dir>/grants.json`, outside every webview fs scope.
pub struct DbGrants {
    file: PathBuf,
    registry: Mutex<crate::grants::Registry>,
}

impl DbGrants {
    pub fn load(file: PathBuf) -> Self {
        let registry = Mutex::new(crate::grants::Registry::load(&file));
        Self { file, registry }
    }

    /// Never held across an await (std mutex).
    pub fn registry(&self) -> std::sync::MutexGuard<'_, crate::grants::Registry> {
        self.registry.lock().unwrap()
    }

    fn save(&self, r: &crate::grants::Registry) -> Result<(), String> {
        r.save(&self.file).map_err(|e| format!("Could not save the file access list: {e}"))
    }

    /// Binds an unbound grant to the profile that was just saved with it.
    pub fn bind(&self, grant: &str, profile_id: &str) {
        let mut r = self.registry();
        if r.bind(grant, profile_id).unwrap_or(false) {
            let _ = self.save(&r);
        }
    }

    pub fn remove_profile(&self, profile_id: &str) {
        let mut r = self.registry();
        if r.remove_profile(profile_id) > 0 {
            let _ = self.save(&r);
        }
    }
}

fn now_seconds() -> u64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PickedDatabase {
    grant_id: String,
    name: String,
    size: u64,
}

/// Native open dialog for a database file. Registers a read-only grant and returns its id and the file's name —
/// never the path (§13 rule 6).
#[tauri::command]
pub async fn db_pick_file(app: AppHandle, grants: State<'_, DbGrants>) -> Result<Option<PickedDatabase>, String> {
    use tauri_plugin_dialog::DialogExt;
    let (tx, rx) = tokio::sync::oneshot::channel();
    app.dialog()
        .file()
        .add_filter("Database files", &["db", "sqlite", "sqlite3", "db3", "duckdb"])
        .add_filter("All files", &["*"])
        .pick_file(move |p| {
            let _ = tx.send(p);
        });
    let Some(picked) = rx.await.map_err(|e| e.to_string())? else { return Ok(None) };
    let path = picked.into_path().map_err(|e| e.to_string())?;
    let size = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    let mut r = grants.registry();
    let (grant_id, name) = r.register(&path, new_token(), now_seconds()).map_err(|e| e.to_string())?;
    grants.save(&r)?;
    Ok(Some(PickedDatabase { grant_id, name, size }))
}

/// "Allow writes to this file": the shell asks in its own dialog and records the answer; the webview only learns the
/// outcome and can never answer for the user (its dialog:allow-ask is not the consent path). True = writes allowed.
#[tauri::command]
pub async fn db_grant_write(app: AppHandle, grant_id: String, grants: State<'_, DbGrants>) -> Result<bool, String> {
    use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
    let name = {
        let r = grants.registry();
        let g = r.get(&grant_id).ok_or_else(|| crate::grants::GrantError::Unknown.message().to_string())?;
        g.path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
    };
    let (tx, rx) = tokio::sync::oneshot::channel();
    app.dialog()
        .message(format!(
            "Querithm will be able to change \"{name}\": your statements, grid edits and imports write to this database file and \
             to the journal files the database keeps beside it. No other file is written.\n\nThe connection opens read-only until you allow this."
        ))
        .title("Allow writes to this file?")
        .kind(MessageDialogKind::Warning)
        .buttons(MessageDialogButtons::OkCancelCustom("Allow writes".into(), "Keep read-only".into()))
        .show(move |allowed| {
            let _ = tx.send(allowed);
        });
    if !rx.await.map_err(|e| e.to_string())? {
        return Ok(false);
    }
    let mut r = grants.registry();
    r.allow_writes(&grant_id, now_seconds()).map_err(|e| e.message().to_string())?;
    grants.save(&r)?;
    Ok(true)
}

/// Back to read-only; no dialog needed to reduce access.
#[tauri::command]
pub fn db_grant_read_only(grant_id: String, grants: State<'_, DbGrants>) -> Result<(), String> {
    let mut r = grants.registry();
    r.read_only(&grant_id).map_err(|e| e.message().to_string())?;
    grants.save(&r)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn params(v: Value) -> Value {
        v
    }

    #[test]
    fn injects_the_path_of_a_live_token_and_strips_forged_paths() {
        let files = PickedFiles::default();
        let token = files.register(PathBuf::from("/data/orders.csv"));
        let mut p = params(json!({ "fileToken": token, "filePath": "/etc/shadow" }));
        inject("import.inspect", &mut p, &files).unwrap();
        assert_eq!(p["filePath"], "/data/orders.csv");
    }

    #[test]
    fn an_import_without_a_token_that_resolves_is_refused_not_passed_on() {
        let files = PickedFiles::default();
        for method in ["import.inspect", "import.run"] {
            for p in [json!({ "filePath": "/etc/shadow" }), json!({}), json!({ "fileToken": null }), json!({ "fileToken": 7 }), json!(null)] {
                let mut p = params(p);
                let err = inject(method, &mut p, &files).unwrap_err();
                assert_eq!(err["data"]["code"], "fileToken", "{method} {p}");
            }
        }
        // The other import methods name no file.
        let mut check = params(json!({ "sessionId": "s", "schema": "dbo", "table": "t" }));
        assert_eq!(inject("import.checkTarget", &mut check, &files).unwrap(), None);
        let mut cancel = params(json!({ "jobId": "j" }));
        assert_eq!(inject("import.cancel", &mut cancel, &files).unwrap(), None);
    }

    #[test]
    fn unknown_and_reused_tokens_are_refused() {
        let files = PickedFiles::default();
        let mut p = params(json!({ "fileToken": "deadbeef" }));
        assert!(inject("import.inspect", &mut p, &files).is_err());
        let token = files.register(PathBuf::from("/data/a.csv"));
        let mut a = params(json!({ "fileToken": token }));
        inject("import.inspect", &mut a, &files).unwrap();
        let mut b = params(json!({ "fileToken": token }));
        let held = inject("import.run", &mut b, &files).unwrap();
        assert_eq!(held.as_deref(), Some(token.as_str()));
        let mut busy = params(json!({ "fileToken": token }));
        assert!(inject("import.run", &mut busy, &files).is_err(), "one import.run at a time");
        files.settle_run(&token, true);
        let mut c = params(json!({ "fileToken": token }));
        assert!(inject("import.run", &mut c, &files).is_err(), "a token runs one import only");
        let mut d = params(json!({ "fileToken": token }));
        assert!(inject("import.inspect", &mut d, &files).is_err());
    }

    #[test]
    fn a_prod_guard_block_keeps_the_token_for_the_confirmed_retry() {
        let files = PickedFiles::default();
        let token = files.register(PathBuf::from("/data/a.csv"));
        let mut first = params(json!({ "fileToken": token, "confirmedProd": false }));
        let held = inject("import.run", &mut first, &files).unwrap().unwrap();
        let blocked: Result<Value, Value> = Ok(json!({ "jobId": null, "blocked": { "reason": "prodWrite" } }));
        assert!(!run_started(&blocked));
        files.settle_run(&held, run_started(&blocked));
        let mut retry = params(json!({ "fileToken": token, "confirmedProd": true }));
        let held = inject("import.run", &mut retry, &files).unwrap().unwrap();
        assert_eq!(retry["filePath"], "/data/a.csv");
        let started: Result<Value, Value> = Ok(json!({ "jobId": "j1" }));
        assert!(run_started(&started));
        files.settle_run(&held, run_started(&started));
        let mut again = params(json!({ "fileToken": token }));
        assert!(inject("import.run", &mut again, &files).is_err());
    }

    #[test]
    fn a_failed_run_call_releases_the_token() {
        let files = PickedFiles::default();
        let token = files.register(PathBuf::from("/data/a.csv"));
        let mut p = params(json!({ "fileToken": token }));
        let held = inject("import.run", &mut p, &files).unwrap().unwrap();
        files.settle_run(&held, run_started(&Err(json!({ "code": -32000 }))));
        let mut retry = params(json!({ "fileToken": token }));
        assert!(inject("import.run", &mut retry, &files).is_ok());
    }

    #[test]
    fn schema_compare_sides_get_their_paths_and_tokens_stay_usable() {
        let files = PickedFiles::default();
        let token = files.register(PathBuf::from("/data/sales.qschema.json"));
        let mut p = params(json!({ "source": { "fileToken": token, "filePath": "/etc/shadow" }, "target": { "sessionId": "s", "filePath": "/etc/passwd" } }));
        inject("schemaCompare.run", &mut p, &files).unwrap();
        assert_eq!(p["source"]["filePath"], "/data/sales.qschema.json");
        assert!(p["target"].get("filePath").is_none());
        let mut again = params(json!({ "source": { "fileToken": token } }));
        inject("schemaCompare.run", &mut again, &files).unwrap();
        assert_eq!(again["source"]["filePath"], "/data/sales.qschema.json");
        let mut bad = params(json!({ "target": { "fileToken": "nope" } }));
        assert!(inject("schemaCompare.run", &mut bad, &files).is_err());
        let mut not_a_token = params(json!({ "source": { "fileToken": 7, "filePath": "/etc/passwd" }, "target": { "sessionId": "s" } }));
        assert_eq!(inject("schemaCompare.run", &mut not_a_token, &files).unwrap_err()["data"]["code"], "fileToken");
        let mut live = params(json!({ "source": { "sessionId": "a", "fileToken": null }, "target": { "sessionId": "b" } }));
        inject("schemaCompare.run", &mut live, &files).unwrap();
        assert!(live["source"].get("filePath").is_none() && live["target"].get("filePath").is_none());
    }

    #[test]
    fn other_methods_are_untouched() {
        let files = PickedFiles::default();
        let mut p = params(json!({ "filePath": "/x", "fileToken": "t" }));
        inject("query.execute", &mut p, &files).unwrap();
        assert_eq!(p["filePath"], "/x");
    }
}
