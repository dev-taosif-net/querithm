// The window's colour at start (§16 start screen). The main window is shown before the webview has painted anything
// (window_state.rs); until then it shows its own background, which had no relation to the app's theme. The theme is a
// setting in the engine's store, which the shell never reads (§4), so the webview tells the shell the theme it loaded
// (`app_theme_keep`, src/app/theme.ts) and the shell keeps that one word in `<app config dir>/theme`, beside
// grants.json and outside every webview fs scope. At the next start the window and the webview get that theme's
// background before the window is shown.
//
// The file is only a copy: missing, unreadable or holding anything else, the window starts dark (the setting's
// default), and the webview corrects the colour as soon as it paints. It holds one of three words and nothing else.
use std::path::Path;

use tauri::utils::config::Color;
use tauri::{AppHandle, Manager, Runtime, Theme, WebviewWindow};

use crate::shell_log;

/// Beside grants.json and window-state.json in the app config directory.
pub const FILE: &str = "theme";

/// `--q-bg` of each theme (src/theme/tokens.css); scripts/__tests__/startScreen.test.ts keeps them in step.
pub const DARK_BG: (u8, u8, u8) = (0x0f, 0x11, 0x17);
pub const LIGHT_BG: (u8, u8, u8) = (0xff, 0xff, 0xff);

/// The Theme setting as the webview names it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kept {
    Dark,
    Light,
    System,
}

/// The setting from its word; `None` for anything else (the file and the command's argument are both untrusted).
pub fn parse(text: &str) -> Option<Kept> {
    match text.trim() {
        "dark" => Some(Kept::Dark),
        "light" => Some(Kept::Light),
        "system" => Some(Kept::System),
        _ => None,
    }
}

fn word(kept: Kept) -> &'static str {
    match kept {
        Kept::Dark => "dark",
        Kept::Light => "light",
        Kept::System => "system",
    }
}

/// The background the window starts with: nothing kept is the setting's default, dark; "system" follows the OS.
pub fn background(kept: Option<Kept>, os_dark: bool) -> (u8, u8, u8) {
    match kept {
        Some(Kept::Light) => LIGHT_BG,
        Some(Kept::System) if !os_dark => LIGHT_BG,
        _ => DARK_BG,
    }
}

/// The theme kept in `file`, if it holds one.
pub fn read(file: &Path) -> Option<Kept> {
    parse(&std::fs::read_to_string(file).ok()?)
}

/// Gives the hidden main window (and its webview) the kept theme's background. Called before it is shown; never
/// fails the start: a colour not set leaves the window as it was before this existed.
pub fn apply<R: Runtime>(window: &WebviewWindow<R>, config_dir: Option<&Path>) {
    let kept = config_dir.and_then(|d| read(&d.join(FILE)));
    // The OS theme is only asked for when the setting follows it.
    let os_dark = kept == Some(Kept::System) && !matches!(window.theme(), Ok(Theme::Light));
    let (r, g, b) = background(kept, os_dark);
    if let Err(e) = window.set_background_color(Some(Color(r, g, b, 255))) {
        shell_log::write("warn", &format!("window start colour not set: {e}"));
    }
}

/// The webview loaded the Theme setting (or the user changed it): kept for the next start.
#[tauri::command]
pub async fn app_theme_keep(app: AppHandle, theme: String) -> Result<(), String> {
    let kept = parse(&theme).ok_or_else(|| "unknown theme".to_string())?;
    let dir = app.path().app_config_dir().map_err(|e| e.to_string())?;
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    std::fs::write(dir.join(FILE), word(kept)).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_the_three_theme_words_are_read() {
        assert_eq!(parse("dark"), Some(Kept::Dark));
        assert_eq!(parse("light\n"), Some(Kept::Light));
        assert_eq!(parse(" system "), Some(Kept::System));
        for other in ["", "Light", "blue", "light dark", "../grants.json", "\0"] {
            assert_eq!(parse(other), None, "{other:?}");
        }
    }

    #[test]
    fn every_setting_is_written_as_the_word_it_is_read_from() {
        for kept in [Kept::Dark, Kept::Light, Kept::System] {
            assert_eq!(parse(word(kept)), Some(kept));
        }
    }

    #[test]
    fn nothing_kept_starts_dark_like_the_default_setting() {
        assert_eq!(background(None, false), DARK_BG);
        assert_eq!(background(None, true), DARK_BG);
    }

    #[test]
    fn dark_and_light_ignore_the_os_theme() {
        assert_eq!(background(Some(Kept::Dark), false), DARK_BG);
        assert_eq!(background(Some(Kept::Light), true), LIGHT_BG);
    }

    #[test]
    fn system_follows_the_os_theme() {
        assert_eq!(background(Some(Kept::System), true), DARK_BG);
        assert_eq!(background(Some(Kept::System), false), LIGHT_BG);
    }

    #[test]
    fn a_missing_or_damaged_file_keeps_nothing() {
        let dir = std::env::temp_dir().join(format!("querithm-theme-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join(FILE);
        assert_eq!(read(&file), None);
        std::fs::write(&file, [0xff, 0xfe, 0x00]).unwrap();
        assert_eq!(read(&file), None);
        std::fs::write(&file, "light").unwrap();
        assert_eq!(read(&file), Some(Kept::Light));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
