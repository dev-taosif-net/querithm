// Querithm shell (§4): owns the window, the secrets and the sidecar's life. No SQL, no parsing, no drivers.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod close;
mod files;
mod grants;
mod injection;
mod instance;
mod ipc;
mod keyring;
mod native;
mod shell_log;
mod sidecar;
mod updater;

use tauri::{Manager, RunEvent, WindowEvent};

fn main() {
    // M18/M21: a panic leaves a crash record in the shell log before `panic = "abort"` ends the process.
    shell_log::install_panic_hook();
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .manage(ipc::Relay::default())
        .manage(sidecar::Supervisor::default())
        .manage(files::PickedFiles::default())
        .manage(updater::Updates::default())
        .manage(close::CloseGuard::default())
        .setup(|app| {
            // Phase D0c: the database-file grant registry, in the config directory no webview fs scope reaches.
            let grants = app.path().app_config_dir()?.join("grants.json");
            app.manage(files::DbGrants::load(grants));
            let data = app.path().data_dir().map(|d| d.join("querithm"));
            if let Ok(data) = &data {
                shell_log::init(data.join("logs"));
            }
            // M23: the first instance owns the saved tabs; a second window starts with a new tab.
            app.manage(data.as_deref().map(instance::claim).unwrap_or(instance::Workspace::UNCLAIMED));
            // Spawned on the main thread: Linux PR_SET_PDEATHSIG fires when the *spawning thread* exits (§4 step 4). A
            // spawn that fails (M18: a quarantined or missing binary) never aborts the app: the window opens and says why.
            sidecar::start(app.handle());
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            ipc::rpc,
            ipc::results_next,
            ipc::results_discard,
            sidecar::sidecar_status,
            sidecar::sidecar_restart,
            sidecar::app_info,
            keyring::secret_set,
            keyring::secret_delete,
            keyring::secret_exists,
            keyring::secret_end_connection,
            files::import_pick_file,
            files::compare_pick_file,
            files::db_pick_file,
            files::db_grant_write,
            files::db_grant_read_only,
            updater::update_check,
            updater::update_download,
            updater::update_install,
            close::app_close_ack,
            close::app_close_answer,
        ])
        .build(tauri::generate_context!())
        .expect("failed to build the Querithm shell");

    app.run(|handle, event| match event {
        // H4: closing the main window first asks the webview what closing would end (src/app/quit.ts).
        RunEvent::WindowEvent { label, event: WindowEvent::CloseRequested { api, .. }, .. } if label == close::MAIN => {
            close::on_request(handle, || api.prevent_close());
        }
        // The OS quitting the app while the window is open (macOS Quit) asks the same; a programmatic exit or restart
        // (code set) never waits.
        RunEvent::ExitRequested { code: None, api, .. } if handle.get_webview_window(close::MAIN).is_some() => {
            close::on_request(handle, || api.prevent_exit());
        }
        RunEvent::Exit => {
            // §4 step 5: system.shutdown, wait 2 s, then kill.
            handle.state::<sidecar::Supervisor>().shutdown();
        }
        _ => {}
    });
}
