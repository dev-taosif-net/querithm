// Credential isolation at the native RPC boundary. The webview can call RPC directly, so endpoint changes must
// retire the previous credentials here as well as in the editor. This module is independent of Tauri/GTK.
use std::collections::HashMap;
use std::future::Future;
use std::sync::{Arc, Mutex, OnceLock, Weak};
use serde_json::{json, Value};
use tokio::sync::{Mutex as AsyncMutex, OwnedMutexGuard};

/// Public commands address a profile or its SSH secret. Timed envelope accounts belong only to the shell.
pub fn valid_account(id: &str) -> bool {
    let base = id.strip_suffix(":ssh").unwrap_or(id);
    !base.is_empty() && base.len() <= 54 && base.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
}

fn enum_name(p: &Value, key: &str, default: &str) -> String {
    p.get(key).and_then(Value::as_str).unwrap_or(default).to_ascii_lowercase()
}
fn field(p: &Value, key: &str) -> Value { p.get(key).cloned().unwrap_or(Value::Null) }
fn host(p: &Value) -> String { p.get("host").and_then(Value::as_str).unwrap_or("").trim().to_lowercase() }
fn server_host(p: &Value) -> String {
    let raw = p.get("host").and_then(Value::as_str).unwrap_or("").trim();
    let socket = enum_name(p, "engine", "mssql") == "pg"
        && raw.split(',').any(|h| h.trim_start().starts_with('/') || h.trim_start().starts_with('@'));
    if socket { raw.to_owned() } else { raw.to_lowercase() }
}

/// Only connection identity and protection: renames, environment and retention changes keep their credentials.
pub fn same_endpoint(a: &Value, b: &Value) -> bool {
    fn key(p: &Value) -> Value {
        let location = enum_name(p, "location", "server");
        let server = location == "server";
        let ssh = p.get("ssh").filter(|v| v.is_object()).map(|s| json!([
            host(s), s.get("port").filter(|v| !v.is_null()).cloned().unwrap_or(json!(22)),
            field(s, "username"), enum_name(s, "auth", "password"), field(s, "keyPath")
        ]));
        json!([
            enum_name(p, "engine", "mssql"), location, p.pointer("/file/grantId"),
            if server { Some(server_host(p)) } else { None }, field(p, "port"), field(p, "username"),
            enum_name(p, "auth", "sql"), enum_name(p, "tlsMode", "default"),
            p.get("trustCert").and_then(Value::as_bool).unwrap_or(false), field(p, "caPath"),
            p.pointer("/options/hostNameInCertificate"), ssh
        ])
    }
    key(a) == key(b)
}

/// Serialize saves, deletes and secret injection for one profile across the entire RPC, including its awaits.
/// Dead gates are weak references and are removed on the next operation; other profiles never wait on this one.
pub async fn lock_profile(method: &str, params: &Value) -> Option<OwnedMutexGuard<()>> {
    let key = match method {
        "profile.save" => "id",
        "profile.delete" | "session.open" | "profile.test" => "profileId",
        _ => return None,
    };
    let id = params.get(key).and_then(Value::as_str).filter(|s| !s.is_empty())?;
    static GATES: OnceLock<Mutex<HashMap<String, Weak<AsyncMutex<()>>>>> = OnceLock::new();
    let gate = {
        let mut gates = GATES.get_or_init(|| Mutex::new(HashMap::new())).lock().unwrap();
        gates.retain(|_, gate| gate.strong_count() > 0);
        match gates.get(id).and_then(Weak::upgrade) {
            Some(gate) => gate,
            None => {
                let gate = Arc::new(AsyncMutex::new(()));
                gates.insert(id.to_owned(), Arc::downgrade(&gate));
                gate
            }
        }
    };
    Some(gate.lock_owned().await)
}

/// Fetch the previous profile from the trusted sidecar, never from a webview-controlled cache. A keyring failure
/// aborts before profile.save is forwarded, so an old password cannot become attached to a new endpoint.
pub async fn prepare_save<F, FF, R, RF>(edited: &Value, fetch: F, retire: R) -> Result<(), Value>
where
    F: FnOnce() -> FF, FF: Future<Output = Result<Value, Value>>,
    R: FnOnce(String) -> RF, RF: Future<Output = Result<(), Value>>,
{
    let Some(id) = edited.get("id").and_then(Value::as_str).filter(|s| !s.is_empty()) else { return Ok(()) };
    let profiles = fetch().await?;
    let profiles = profiles.as_array().ok_or_else(|| json!({ "code": -32001, "message": "Could not verify the saved profile before editing it.", "data": { "code": "invalidProfile" } }))?;
    if let Some(before) = profiles.iter().find(|p| p.get("id").and_then(Value::as_str) == Some(id)) {
        let needs_db = matches!(enum_name(before, "auth", "sql").as_str(), "sql" | "password" | "entratoken");
        if !same_endpoint(before, edited) && (needs_db || before.get("ssh").is_some_and(Value::is_object)) {
            retire(id.to_owned()).await?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicBool, Ordering};

    fn profile() -> Value { json!({ "id": "victim", "engine": "pg", "host": "db.invalid", "auth": "password", "username": "u", "tlsMode": "verifyFull", "ssh": { "host": "jump.invalid", "username": "j", "auth": "password" } }) }

    #[test]
    fn timed_envelopes_and_non_profile_accounts_are_not_public() {
        for account in ["victim:timed", "victim:ssh:timed", "victim:ssh:ssh", "../victim", "victim\0x", ""] {
            assert!(!valid_account(account), "{account:?}");
        }
        assert!(valid_account("victim"));
        assert!(valid_account("victim:ssh"));
        assert!(valid_account(&format!("{}:ssh", "x".repeat(54))));
        assert!(!valid_account(&"x".repeat(55)));
    }

    #[tokio::test]
    async fn forged_save_retires_credentials_before_the_endpoint_can_change() {
        let before = profile();
        let mut edited = before.clone();
        edited["host"] = json!("attacker.invalid");
        let retired = &AtomicBool::new(false);
        prepare_save(&edited, || async { Ok(json!([before])) }, |id| async move {
            assert_eq!(id, "victim");
            retired.store(true, Ordering::SeqCst);
            Ok(())
        }).await.unwrap();
        assert!(retired.load(Ordering::SeqCst));
    }

    #[tokio::test]
    async fn a_locked_keyring_aborts_the_edit() {
        let before = profile();
        let mut edited = before.clone();
        edited["ssh"]["host"] = json!("attacker.invalid");
        let error = prepare_save(&edited, || async { Ok(json!([before])) }, |_| async { Err(json!({"data":{"code":"keyring"}})) }).await.unwrap_err();
        assert_eq!(error["data"]["code"], "keyring");
    }

    #[test]
    fn all_credential_destination_and_protection_fields_are_bound() {
        let before = profile();
        for (pointer, value) in [
            ("/host", json!("other.invalid")), ("/port", json!(5433)), ("/username", json!("other")),
            ("/engine", json!("mySql")), ("/auth", json!("entraToken")), ("/tlsMode", json!("disable")),
            ("/trustCert", json!(true)), ("/caPath", json!("/tmp/attacker.pem")),
            ("/ssh/host", json!("other.invalid")), ("/ssh/port", json!(2222)), ("/ssh/username", json!("other")),
            ("/ssh/auth", json!("privateKey")), ("/ssh/keyPath", json!("/tmp/key")),
            ("/options", json!({"hostNameInCertificate":"attacker.invalid"})),
        ] {
            let mut edited = before.clone();
            let parts: Vec<_> = pointer.trim_start_matches('/').split('/').collect();
            if parts.len() == 1 { edited[parts[0]] = value; } else { edited[parts[0]][parts[1]] = value; }
            assert!(!same_endpoint(&before, &edited), "{pointer}");
        }
        let mut renamed = before.clone();
        renamed["name"] = json!("renamed");
        renamed["environment"] = json!("prod");
        renamed["passwordRetention"] = json!("once");
        assert!(same_endpoint(&before, &renamed));
    }

    #[test]
    fn unix_socket_paths_are_case_sensitive_even_though_dns_names_are_not() {
        for host in ["/tmp/ProdSocket", "@ProdSocket", "localhost,/tmp/ProdSocket"] {
            let mut a = profile();
            a["host"] = json!(host);
            let mut b = a.clone();
            b["host"] = json!(host.to_lowercase());
            assert!(!same_endpoint(&a, &b), "{host}");
        }
    }

    #[tokio::test]
    async fn cosmetic_edits_do_not_touch_the_vault() {
        let before = profile();
        let mut edited = before.clone();
        edited["name"] = json!("renamed");
        prepare_save(&edited, || async { Ok(json!([before])) }, |_| async { panic!("cosmetic edit touched the vault") }).await.unwrap();
    }

    #[tokio::test]
    async fn injection_waits_for_the_same_profiles_save_but_other_profiles_continue() {
        let save = json!({"id":"gate-victim"});
        let held = lock_profile("profile.save", &save).await.unwrap();
        let waiting = tokio::spawn(async { lock_profile("session.open", &json!({"profileId":"gate-victim"})).await });
        tokio::task::yield_now().await;
        assert!(!waiting.is_finished());
        let other = lock_profile("session.open", &json!({"profileId":"other"})).await.unwrap();
        drop(other);
        drop(held);
        assert!(waiting.await.unwrap().is_some());
    }
}
