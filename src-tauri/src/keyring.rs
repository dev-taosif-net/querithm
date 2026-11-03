// Session credentials stay in memory. Persistent and timed credentials use the OS keyring.
use std::collections::HashMap;
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::sync::Mutex;

#[derive(Clone, Debug, PartialEq)]
enum Policy { Once, Session, Duration(u64), Forever }
struct Credential {
    policy: Policy,
    secret: Option<String>,
    entered: Instant,
    expires_at: Option<u64>,
    /// R1-9: the profile was deleted. A caller that found this slot before it went (a secret_set waiting on its lock)
    /// must not write the secret back to the vault.
    forgotten: bool,
}
impl Credential {
    fn new(policy: Policy) -> Self {
        Self { policy, secret: None, entered: Instant::now(), expires_at: None, forgotten: false }
    }
    fn valid(&self, now: Instant, wall_now: u64) -> bool {
        self.expires_at.map_or(true, |expiry| wall_now < expiry)
            && !matches!(self.policy, Policy::Duration(minutes) if now.duration_since(self.entered) >= Duration::from_secs(minutes * 60))
    }
}
// Timed credentials have their own account so a legacy reader can never treat the envelope as a password.
#[derive(serde::Serialize, serde::Deserialize)]
struct TimedSecret { secret: String, expires_at: u64 }
fn now_seconds() -> u64 { SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs() }
fn account(id: &str, policy: &Policy) -> Option<String> {
    match policy { Policy::Forever => Some(id.into()), Policy::Duration(_) => Some(format!("{id}:timed")), _ => None }
}
/// One account's credential. Its lock is held across that account's keyring calls (an unlock prompt may wait for the
/// user), so two callers of one account share one read — the second finds the secret the first loaded — while other
/// accounts go on (L22).
type Slot = Arc<Mutex<Credential>>;
/// The accounts. A std mutex held only to find or insert a slot, never across a keyring call or an await.
fn cache() -> &'static std::sync::Mutex<HashMap<String, Slot>> {
    static CACHE: OnceLock<std::sync::Mutex<HashMap<String, Slot>>> = OnceLock::new();
    CACHE.get_or_init(|| std::sync::Mutex::new(HashMap::new()))
}
fn accounts() -> std::sync::MutexGuard<'static, HashMap<String, Slot>> {
    cache().lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}
/// The account's slot, made with `fresh` when there is none.
fn slot(id: &str, fresh: impl FnOnce() -> Credential) -> Slot {
    accounts().entry(id.to_owned()).or_insert_with(|| Arc::new(Mutex::new(fresh()))).clone()
}
fn existing(id: &str) -> Option<Slot> {
    accounts().get(id).cloned()
}
fn validate_id(id: &str) -> Result<(), String> {
    if id.is_empty() || id.len() > 64 { Err("invalid profile id".into()) } else { Ok(()) }
}
#[cfg(not(test))]
async fn read_disk(id: String) -> Result<Option<String>, String> {
    tauri::async_runtime::spawn_blocking(move || {
        let entry = keyring::Entry::new("querithm", &id).map_err(|e| e.to_string())?;
        match entry.get_password() {
            Ok(s) => Ok(Some(s)),
            Err(keyring::Error::NoEntry) => Ok(None),
            Err(e) => Err(e.to_string()),
        }
    }).await.map_err(|e| e.to_string())?
}
#[cfg(not(test))]
async fn write_disk(id: String, secret: Option<String>) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        let entry = keyring::Entry::new("querithm", &id).map_err(|e| e.to_string())?;
        let result = if let Some(s) = secret { entry.set_password(&s) } else { entry.delete_credential() };
        match result { Ok(()) | Err(keyring::Error::NoEntry) => Ok(()), Err(e) => Err(e.to_string()) }
    }).await.map_err(|e| e.to_string())?
}

async fn load_secret(id: &str, c: &mut Credential) -> Result<Option<String>, String> {
    if !c.valid(Instant::now(), now_seconds()) {
        c.secret = None;
        if let Some(key) = account(id, &c.policy) { write_disk(key, None).await?; }
        return Ok(None);
    }
    if c.secret.is_none() {
        match c.policy {
            Policy::Forever => { c.secret = read_disk(id.into()).await?; }
            Policy::Duration(_) => {
                if let Some(raw) = read_disk(format!("{id}:timed")).await? {
                    let saved: TimedSecret = serde_json::from_str(&raw).map_err(|_| "Invalid timed credential".to_string())?;
                    c.expires_at = Some(saved.expires_at);
                    if now_seconds() >= saved.expires_at {
                        write_disk(format!("{id}:timed"), None).await?;
                        return Ok(None);
                    }
                    c.secret = Some(saved.secret);
                }
            }
            _ => {}
        }
    }
    Ok(c.secret.clone())
}
async fn store_secret(id: &str, c: &mut Credential, secret: String) -> Result<(), String> {
    if c.forgotten {
        return Err("The profile was deleted; its password was not saved.".into());
    }
    let expiry = match c.policy { Policy::Duration(minutes) => Some(now_seconds() + minutes * 60), _ => None };
    if let Some(key) = account(id, &c.policy) {
        let value = if let Some(expires_at) = expiry {
            serde_json::to_string(&TimedSecret { secret: secret.clone(), expires_at }).map_err(|e| e.to_string())?
        } else { secret.clone() };
        write_disk(key, Some(value)).await?;
    }
    c.secret = Some(secret);
    c.expires_at = expiry;
    c.entered = Instant::now();
    Ok(())
}

// Loading a profile does not renew the deadline; changing the policy migrates a still-valid credential.
pub async fn configure(profile: &serde_json::Value, migrate: bool) -> Result<(), String> {
    let Some(id) = profile.get("id").and_then(|v| v.as_str()) else { return Ok(()) };
    validate_id(id)?;
    let policy = match profile.get("passwordRetention").and_then(|v| v.as_str()).unwrap_or("forever") {
        "once" => Policy::Once,
        "session" => Policy::Session,
        "duration" => Policy::Duration(profile.get("passwordRetentionMinutes").and_then(|v| v.as_u64()).filter(|n| *n >= 1 && *n <= 525600).ok_or("Invalid password duration")?),
        "forever" => Policy::Forever,
        _ => return Err("Invalid password retention".into()),
    };
    // A profile seen for the first time gets a fresh credential of its policy; nothing to migrate.
    let slot = slot(id, || Credential::new(policy.clone()));
    let mut old = slot.lock().await;
    if old.policy == policy { return Ok(()) }
    let mut next = Credential::new(policy);
    if migrate {
        let secret = load_secret(id, &mut old).await?;
        let old_account = account(id, &old.policy);
        // Delete old persistent storage before adopting a shorter retention policy.
        if old_account != account(id, &next.policy) {
            if let Some(key) = old_account { write_disk(key, None).await?; }
        }
        if let Some(secret) = secret { store_secret(id, &mut next, secret).await?; }
    }
    *old = next;
    Ok(())
}

pub async fn get(id: String) -> Result<Option<String>, String> {
    validate_id(&id)?;
    let slot = slot(&id, || Credential::new(Policy::Forever));
    let mut c = slot.lock().await;
    load_secret(&id, &mut c).await
}
#[tauri::command]
pub async fn secret_set(profile_id: String, secret: String) -> Result<(), String> {
    validate_id(&profile_id)?;
    let slot = slot(&profile_id, || Credential::new(Policy::Forever));
    let mut c = slot.lock().await;
    store_secret(&profile_id, &mut c, secret).await
}
#[tauri::command]
pub async fn secret_delete(profile_id: String) -> Result<(), String> {
    validate_id(&profile_id)?;
    // Held while the entries go, so a read of this account meanwhile cannot bring the old secret back.
    let slot = slot(&profile_id, || Credential::new(Policy::Forever));
    let mut c = slot.lock().await;
    c.secret = None;
    let plain = write_disk(profile_id.clone(), None).await;
    let timed = write_disk(format!("{profile_id}:timed"), None).await;
    plain.and(timed)
}
/// L2: a deleted profile takes every vault entry along — `<id>`, `<id>:timed`, `<id>:ssh`, `<id>:ssh:timed` — and its
/// memory entries. Called by the shell after the sidecar deleted the profile; every entry is tried, and the first failure
/// is returned (the caller reports it: the profile is gone, the secret may not be).
pub async fn forget_profile(profile_id: &str) -> Result<(), String> {
    validate_id(profile_id)?;
    let ssh = format!("{profile_id}:ssh");
    // Both accounts' slots are held while their entries go (a read meanwhile waits, then finds nothing); the memory
    // entries go whatever the vault answers.
    let (db_slot, ssh_slot) = (slot(profile_id, || Credential::new(Policy::Forever)), slot(&ssh, || Credential::new(Policy::Forever)));
    let (mut db, mut tunnel) = (db_slot.lock().await, ssh_slot.lock().await);
    db.secret = None;
    tunnel.secret = None;
    // Callers already waiting on these slots find them forgotten; new callers get fresh slots once they leave the map.
    db.forgotten = true;
    tunnel.forgotten = true;
    let mut first_error = None;
    for account in [profile_id.to_owned(), format!("{profile_id}:timed"), ssh.clone(), format!("{ssh}:timed")] {
        if let Err(e) = write_disk(account, None).await {
            first_error.get_or_insert(e);
        }
    }
    {
        let mut all = accounts();
        all.remove(profile_id);
        all.remove(&ssh);
    }
    first_error.map_or(Ok(()), Err)
}
#[tauri::command]
pub async fn secret_exists(profile_id: String) -> Result<bool, String> { Ok(get(profile_id).await?.is_some()) }
#[tauri::command]
pub async fn secret_end_connection(profile_id: String) -> Result<(), String> {
    validate_id(&profile_id)?;
    if let Some(slot) = existing(&profile_id) {
        let mut c = slot.lock().await;
        if c.policy == Policy::Once { c.secret = None; }
    }
    Ok(())
}

// Tests replace only the operating-system vault, exercising the actual retention and restart logic.
#[cfg(test)]
fn test_disk() -> &'static Mutex<HashMap<String, String>> {
    static DISK: OnceLock<Mutex<HashMap<String, String>>> = OnceLock::new();
    DISK.get_or_init(|| Mutex::new(HashMap::new()))
}
/// L22 tests: vault reads per account, and accounts whose read waits (an unlock prompt the user has not answered).
#[cfg(test)]
fn test_reads() -> &'static std::sync::Mutex<HashMap<String, usize>> {
    static READS: OnceLock<std::sync::Mutex<HashMap<String, usize>>> = OnceLock::new();
    READS.get_or_init(|| std::sync::Mutex::new(HashMap::new()))
}
#[cfg(test)]
fn test_prompts() -> &'static std::sync::Mutex<HashMap<String, Arc<tokio::sync::Semaphore>>> {
    static PROMPTS: OnceLock<std::sync::Mutex<HashMap<String, Arc<tokio::sync::Semaphore>>>> = OnceLock::new();
    PROMPTS.get_or_init(|| std::sync::Mutex::new(HashMap::new()))
}
#[cfg(test)]
async fn read_disk(id: String) -> Result<Option<String>, String> {
    *test_reads().lock().unwrap().entry(id.clone()).or_default() += 1;
    let prompt = test_prompts().lock().unwrap().get(&id).cloned();
    if let Some(answered) = prompt { drop(answered.acquire().await.map_err(|e| e.to_string())?); }
    Ok(test_disk().lock().await.get(&id).cloned())
}
/// Accounts whose vault operation fails in tests (a locked or unavailable keyring).
#[cfg(test)]
fn test_failing() -> &'static Mutex<std::collections::HashSet<String>> {
    static FAILING: OnceLock<Mutex<std::collections::HashSet<String>>> = OnceLock::new();
    FAILING.get_or_init(|| Mutex::new(std::collections::HashSet::new()))
}
#[cfg(test)]
async fn write_disk(id: String, secret: Option<String>) -> Result<(), String> {
    if test_failing().lock().await.contains(&id) { return Err(format!("the keyring is locked ({id})")); }
    let mut disk = test_disk().lock().await;
    if let Some(s) = secret { disk.insert(id, s); } else { disk.remove(&id); }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    fn profile(id: &str, policy: &str) -> serde_json::Value {
        serde_json::json!({ "id": id, "passwordRetention": policy, "passwordRetentionMinutes": 10080 })
    }
    #[tokio::test]
    async fn seven_days_survives_restart_without_extending_expiry() {
        let p = profile("seven-days", "duration");
        configure(&p, true).await.unwrap();
        secret_set("seven-days".into(), "typed".into()).await.unwrap();
        let raw = read_disk("seven-days:timed".into()).await.unwrap().unwrap();
        let saved: TimedSecret = serde_json::from_str(&raw).unwrap();
        assert!(saved.expires_at >= now_seconds() + 7 * 86400 - 1);
        accounts().remove("seven-days"); // app restart
        configure(&p, false).await.unwrap();
        assert_eq!(get("seven-days".into()).await.unwrap().as_deref(), Some("typed"));
        configure(&p, true).await.unwrap();
        assert_eq!(read_disk("seven-days:timed".into()).await.unwrap().unwrap(), raw);
    }
    #[tokio::test]
    async fn expired_persistent_password_is_removed_after_restart() {
        write_disk("expired:timed".into(), Some(serde_json::to_string(&TimedSecret { secret: "old".into(), expires_at: now_seconds() - 1 }).unwrap())).await.unwrap();
        configure(&profile("expired", "duration"), false).await.unwrap();
        assert!(get("expired".into()).await.unwrap().is_none());
        assert!(read_disk("expired:timed".into()).await.unwrap().is_none());
    }
    #[tokio::test]
    async fn changing_retention_migrates_and_removes_old_storage() {
        configure(&profile("migrate", "forever"), true).await.unwrap();
        secret_set("migrate".into(), "typed".into()).await.unwrap();
        configure(&profile("migrate", "duration"), true).await.unwrap();
        assert!(read_disk("migrate".into()).await.unwrap().is_none());
        assert!(read_disk("migrate:timed".into()).await.unwrap().is_some());
        configure(&profile("migrate", "once"), true).await.unwrap();
        assert!(read_disk("migrate:timed".into()).await.unwrap().is_none());
        assert_eq!(get("migrate".into()).await.unwrap().as_deref(), Some("typed"));
        secret_end_connection("migrate".into()).await.unwrap();
        assert!(get("migrate".into()).await.unwrap().is_none());
    }
    #[tokio::test]
    async fn forget_removes_memory_and_both_vault_entries() {
        configure(&profile("forget", "duration"), true).await.unwrap();
        secret_set("forget".into(), "typed".into()).await.unwrap();
        write_disk("forget".into(), Some("legacy".into())).await.unwrap();
        secret_delete("forget".into()).await.unwrap();
        assert!(get("forget".into()).await.unwrap().is_none());
        assert!(read_disk("forget".into()).await.unwrap().is_none());
        assert!(read_disk("forget:timed".into()).await.unwrap().is_none());
    }
    #[tokio::test]
    async fn deleting_a_profile_removes_every_vault_entry_and_the_memory_cache() {
        configure(&profile("deleted", "duration"), true).await.unwrap();
        secret_set("deleted".into(), "typed".into()).await.unwrap();
        secret_set("deleted:ssh".into(), "passphrase".into()).await.unwrap();
        for account in ["deleted", "deleted:ssh:timed"] {
            write_disk(account.into(), Some("legacy".into())).await.unwrap();
        }
        forget_profile("deleted").await.unwrap();
        for account in ["deleted", "deleted:timed", "deleted:ssh", "deleted:ssh:timed"] {
            assert!(read_disk(account.into()).await.unwrap().is_none(), "{account} left behind");
        }
        let all = accounts();
        assert!(!all.contains_key("deleted") && !all.contains_key("deleted:ssh"), "memory entries left behind");
    }
    #[tokio::test(flavor = "current_thread")]
    async fn a_secret_set_waiting_while_its_profile_is_deleted_never_writes_the_secret_back() {
        // R1-9: forget_profile holds the slot; a secret_set for the same profile waits on that slot (it found it before it
        // left the map), then ran on it and wrote the password of a deleted profile back to the vault.
        let id = "tombstoned";
        let slot = slot(id, || Credential::new(Policy::Forever));
        let held = slot.lock().await;
        let forget = tokio::spawn(async move { forget_profile(id).await });
        for _ in 0..5 { tokio::task::yield_now().await; }
        let set = tokio::spawn(secret_set(id.into(), "typed".into()));
        for _ in 0..5 { tokio::task::yield_now().await; }
        drop(held);
        forget.await.unwrap().unwrap();
        let refused = set.await.unwrap();
        assert!(refused.unwrap_err().contains("deleted"));
        assert!(read_disk(id.into()).await.unwrap().is_none(), "the deleted profile's password came back");
        // A profile made again later with the same id starts from a fresh slot.
        secret_set(id.into(), "again".into()).await.unwrap();
        assert_eq!(get(id.into()).await.unwrap().as_deref(), Some("again"));
        forget_profile(id).await.unwrap();
    }
    #[tokio::test]
    async fn a_keyring_failure_on_delete_is_reported_and_the_other_entries_still_go() {
        secret_set("locked".into(), "typed".into()).await.unwrap();
        secret_set("locked:ssh".into(), "passphrase".into()).await.unwrap();
        test_failing().lock().await.insert("locked".into());
        let result = forget_profile("locked").await;
        test_failing().lock().await.remove("locked");
        assert!(result.unwrap_err().contains("locked"));
        assert!(read_disk("locked:ssh".into()).await.unwrap().is_none());
        assert!(!accounts().contains_key("locked"), "the memory copy goes even when the vault refuses");
    }
    #[tokio::test]
    async fn disconnect_preserves_app_session_password() {
        configure(&profile("session", "session"), true).await.unwrap();
        secret_set("session".into(), "typed".into()).await.unwrap();
        secret_end_connection("session".into()).await.unwrap();
        assert_eq!(get("session".into()).await.unwrap().as_deref(), Some("typed"));
        assert!(read_disk("session".into()).await.unwrap().is_none());
    }
    /// An unanswered unlock prompt for one account: its reads wait until `answer`.
    fn prompt(account: &str) -> Arc<tokio::sync::Semaphore> {
        let gate = Arc::new(tokio::sync::Semaphore::new(0));
        test_prompts().lock().unwrap().insert(account.into(), gate.clone());
        gate
    }
    fn answer(account: &str, gate: &tokio::sync::Semaphore) {
        test_prompts().lock().unwrap().remove(account);
        gate.add_permits(tokio::sync::Semaphore::MAX_PERMITS / 2);
    }
    fn reads(account: &str) -> usize { test_reads().lock().unwrap().get(account).copied().unwrap_or(0) }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn an_unanswered_unlock_prompt_for_one_account_holds_no_other_account() {
        // L22: the cache's one lock was held across the vault call, so every other credential waited for the prompt.
        write_disk("prompted".into(), Some("first".into())).await.unwrap();
        write_disk("other".into(), Some("second".into())).await.unwrap();
        let gate = prompt("prompted");
        let waiting = tokio::spawn(get("prompted".into()));
        while reads("prompted") == 0 { tokio::task::yield_now().await; }
        let quick = tokio::time::timeout(Duration::from_secs(5), async {
            let other = get("other".into()).await.unwrap();
            secret_set("third".into(), "typed".into()).await.unwrap();
            configure(&profile("fourth", "session"), true).await.unwrap();
            secret_end_connection("fourth".into()).await.unwrap();
            other
        }).await;
        assert_eq!(quick.expect("another account waited for the prompt").as_deref(), Some("second"));
        assert!(!waiting.is_finished());
        answer("prompted", &gate);
        assert_eq!(waiting.await.unwrap().unwrap().as_deref(), Some("first"));
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn two_callers_of_one_account_wait_for_one_prompt_and_share_its_answer() {
        write_disk("shared".into(), Some("once".into())).await.unwrap();
        let gate = prompt("shared");
        let first = tokio::spawn(get("shared".into()));
        while reads("shared") == 0 { tokio::task::yield_now().await; }
        let second = tokio::spawn(get("shared".into()));
        tokio::time::sleep(Duration::from_millis(100)).await;
        assert!(!second.is_finished(), "the second caller went to the vault on its own");
        answer("shared", &gate);
        assert_eq!(first.await.unwrap().unwrap().as_deref(), Some("once"));
        assert_eq!(second.await.unwrap().unwrap().as_deref(), Some("once"));
        assert_eq!(reads("shared"), 1, "two vault reads (two prompts) for one account");
    }

    #[test]
    fn deadline_checks_wall_clock_and_monotonic_time() {
        let mut c = Credential::new(Policy::Duration(1));
        c.expires_at = Some(1000);
        assert!(c.valid(c.entered + Duration::from_secs(59), 999));
        assert!(!c.valid(c.entered, 1000));
        assert!(!c.valid(c.entered + Duration::from_secs(60), 900));
    }
}
