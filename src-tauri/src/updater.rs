// In-app updates (§13 rule 10, §16): the shell checks the signed release manifest, downloads the installer for
// this bundle type, stops the sidecar, installs and relaunches. The webview gets three commands and no updater
// permission; the plugin verifies every package against the minisign key in tauri.conf.json.
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde_json::{json, Value};
use tauri::{AppHandle, Emitter, Manager, State};
use tauri_plugin_updater::{Error, Update, UpdaterExt};

use crate::sidecar::{self, Supervisor};

/// Release notes are untrusted text shown as plain text; longer notes are cut here.
const MAX_NOTES: usize = 8_000;
const PROGRESS_EVERY: Duration = Duration::from_millis(200);

#[derive(Default)]
pub struct Updates {
    /// The release found by the last check, and its package once downloaded and verified.
    pending: Mutex<Option<Update>>,
    downloaded: Mutex<Option<(String, Vec<u8>)>>,
    busy: AtomicBool,
}

/// One download or install at a time; released when dropped, also on early returns.
struct Busy<'a>(&'a AtomicBool);

impl Drop for Busy<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

impl Updates {
    fn claim(&self) -> Result<Busy<'_>, Value> {
        if self.busy.swap(true, Ordering::SeqCst) {
            return Err(fail("busy", "The update is already being downloaded or installed."));
        }
        Ok(Busy(&self.busy))
    }

    fn keep_download(&self, version: &str, bytes: Vec<u8>) {
        *self.downloaded.lock().unwrap() = Some((version.to_string(), bytes));
    }

    /// The downloaded package of `version`; a package of another release is dropped.
    fn take_download(&self, version: &str) -> Option<Vec<u8>> {
        let mut slot = self.downloaded.lock().unwrap();
        match slot.take() {
            Some((v, bytes)) if v == version => Some(bytes),
            _ => None,
        }
    }
}

fn fail(kind: &str, message: impl Into<String>) -> Value {
    json!({ "kind": kind, "message": message.into() })
}

/// Maps updater errors to a stable kind for the UI and a sentence a user can act on.
pub fn describe(e: &Error) -> Value {
    match e {
        Error::Reqwest(_) | Error::Network(_) | Error::ReleaseNotFound => {
            fail("network", "Could not reach the update server. Check your internet connection and try again.")
        }
        Error::Minisign(_) | Error::Base64(_) | Error::SignatureUtf8(_) | Error::SignedVersionMismatch { .. } => fail(
            "signature",
            "The downloaded update failed its signature check and was not installed. Download it from the Querithm website instead.",
        ),
        Error::TargetNotFound(_) | Error::TargetsNotFound(_) | Error::UnsupportedOs | Error::UnsupportedArch => {
            fail("unsupported", "This release has no update package for your installation. Download it from the Querithm website.")
        }
        Error::AuthenticationFailed => fail("cancelled", "The update was not installed because the administrator prompt was cancelled."),
        Error::DebInstallFailed | Error::PackageInstallFailed => {
            fail("install", "The package manager could not install the update. Your current version is unchanged.")
        }
        Error::Io(io) if io.kind() == std::io::ErrorKind::PermissionDenied => fail(
            "permission",
            "Querithm has no permission to replace its own files. Download the update from the Querithm website instead.",
        ),
        other => fail("install", format!("The update could not be installed: {other}")),
    }
}

fn truncate(notes: Option<String>) -> Option<String> {
    notes.map(|n| if n.chars().count() > MAX_NOTES { n.chars().take(MAX_NOTES).collect::<String>() + "…" } else { n })
}

fn info(update: &Update) -> Value {
    json!({
        "version": update.version,
        "currentVersion": update.current_version,
        "notes": truncate(update.body.clone()),
        "date": update.raw_json.get("pub_date").and_then(Value::as_str),
    })
}

#[tauri::command]
pub async fn update_check(app: AppHandle, updates: State<'_, Updates>) -> Result<Option<Value>, Value> {
    if cfg!(debug_assertions) {
        return Err(fail("disabled", "Updates are available in installed release builds only."));
    }
    let _busy = updates.claim()?;
    let updater = app.updater().map_err(|e| describe(&e))?;
    let found = updater.check().await.map_err(|e| describe(&e))?;
    let reply = found.as_ref().map(info);
    *updates.pending.lock().unwrap() = found;
    Ok(reply)
}

/// Downloads and verifies the package; the app keeps running. Progress arrives as `update://progress`.
#[tauri::command]
pub async fn update_download(app: AppHandle, updates: State<'_, Updates>) -> Result<(), Value> {
    let _busy = updates.claim()?;
    let update = updates.pending.lock().unwrap().clone().ok_or_else(|| fail("noUpdate", "Check for updates first."))?;
    let mut downloaded = 0u64;
    let mut last = Instant::now() - PROGRESS_EVERY;
    let bytes = update
        .download(
            |chunk, total| {
                downloaded += chunk as u64;
                if last.elapsed() >= PROGRESS_EVERY {
                    last = Instant::now();
                    let _ = app.emit("update://progress", json!({ "downloaded": downloaded, "total": total }));
                }
            },
            || {},
        )
        .await
        .map_err(|e| describe(&e))?; // verifies the signature, so a bad package fails before the engine stops
    let _ = app.emit("update://progress", json!({ "downloaded": downloaded, "total": downloaded }));
    updates.keep_download(&update.version, bytes);
    Ok(())
}

/// Stops the engine, installs the downloaded package and relaunches. On failure the engine is started again.
#[tauri::command]
pub async fn update_install(app: AppHandle, updates: State<'_, Updates>) -> Result<(), Value> {
    let _busy = updates.claim()?;
    let update = updates.pending.lock().unwrap().clone().ok_or_else(|| fail("noUpdate", "Check for updates first."))?;
    let bytes = updates.take_download(&update.version).ok_or_else(|| fail("noDownload", "Download the update first."))?;

    // The installer replaces the sidecar binary, which Windows keeps locked while it runs (§4 step 5 shutdown).
    let handle = app.clone();
    let installed = tauri::async_runtime::spawn_blocking(move || {
        handle.state::<Supervisor>().shutdown();
        update.install(bytes) // Windows: starts the installer and exits the app here.
    })
    .await
    .unwrap_or_else(|e| Err(Error::Io(std::io::Error::other(e.to_string()))));

    match installed {
        Ok(()) => {
            // The update dialog already listed what the restart ends: the window's close guard does not ask again (H4).
            app.state::<crate::close::CloseGuard>().allow();
            app.request_restart();
            Ok(())
        }
        Err(e) => {
            // The current version is still in place: bring the engine back so the user keeps working.
            let _ = sidecar::sidecar_restart(app.clone()).await;
            Err(describe(&e))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn kind(e: Error) -> String {
        describe(&e)["kind"].as_str().unwrap().to_string()
    }

    #[test]
    fn errors_map_to_stable_kinds() {
        assert_eq!(kind(Error::Network("offline".into())), "network");
        assert_eq!(kind(Error::ReleaseNotFound), "network");
        assert_eq!(kind(Error::SignedVersionMismatch { signed: "0.1.1".into(), announced: "9.0.0".into() }), "signature");
        assert_eq!(kind(Error::SignatureUtf8("x".into())), "signature");
        assert_eq!(kind(Error::TargetNotFound("linux-x86_64".into())), "unsupported");
        assert_eq!(kind(Error::AuthenticationFailed), "cancelled");
        assert_eq!(kind(Error::PackageInstallFailed), "install");
        assert_eq!(kind(Error::Io(std::io::Error::from(std::io::ErrorKind::PermissionDenied))), "permission");
        assert_eq!(kind(Error::Io(std::io::Error::from(std::io::ErrorKind::NotFound))), "install");
    }

    #[test]
    fn messages_never_echo_the_signature_or_server_data() {
        let v = describe(&Error::SignatureUtf8("c2VjcmV0".into()));
        assert!(!v["message"].as_str().unwrap().contains("c2VjcmV0"));
        let v = describe(&Error::SignedVersionMismatch { signed: "0.1.1".into(), announced: "9.0.0".into() });
        assert!(!v["message"].as_str().unwrap().contains("9.0.0"));
    }

    #[test]
    fn one_download_or_install_at_a_time() {
        let u = Updates::default();
        let first = u.claim().unwrap();
        assert_eq!(u.claim().err().unwrap()["kind"], "busy");
        drop(first);
        assert!(u.claim().is_ok(), "released on drop, also after an error return");
    }

    #[test]
    fn install_takes_only_the_package_of_the_announced_version() {
        let u = Updates::default();
        assert!(u.take_download("0.1.2").is_none());
        u.keep_download("0.1.2", vec![1, 2, 3]);
        assert!(u.take_download("0.1.3").is_none(), "a package of another release is never installed");
        assert!(u.take_download("0.1.2").is_none(), "and it was dropped");
        u.keep_download("0.1.2", vec![1, 2, 3]);
        assert_eq!(u.take_download("0.1.2"), Some(vec![1, 2, 3]));
        assert!(u.take_download("0.1.2").is_none(), "taken once");
    }

    // tests-data/latest.sample.json is what scripts/release-manifest.mjs writes (a dry run with the release key).
    #[test]
    fn the_release_manifest_parses_with_every_installed_bundle_type() {
        let raw = include_str!("../tests-data/latest.sample.json");
        let release: tauri_plugin_updater::RemoteRelease = serde_json::from_str(raw).unwrap();
        assert!(release.pub_date.is_some());
        for target in [
            "windows-x86_64-nsis", "windows-x86_64-msi", "darwin-aarch64-app", "darwin-x86_64-app",
            "linux-x86_64-appimage", "linux-x86_64-deb", "linux-x86_64-rpm",
        ] {
            assert!(release.download_url(target).unwrap().as_str().starts_with("https://github.com/"), "{target}");
            assert!(!release.signature(target).unwrap().is_empty(), "{target}");
        }
    }

    #[test]
    fn long_release_notes_are_cut() {
        let long = "é".repeat(MAX_NOTES + 10);
        let cut = truncate(Some(long)).unwrap();
        assert_eq!(cut.chars().count(), MAX_NOTES + 1);
        assert!(cut.ends_with('…'));
        assert_eq!(truncate(Some("short".into())).as_deref(), Some("short"));
        assert_eq!(truncate(None), None);
    }
}
