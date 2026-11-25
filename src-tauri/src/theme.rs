// The window's colour at start (§16 start screen). The main window is shown before the webview has painted anything
// (window_state.rs); until then it shows its own background, which had no relation to the app's theme. The theme is a
// setting in the engine's store, which the shell never reads (§4), so the webview tells the shell what to start with
// (`app_theme_keep`, src/app/theme.ts) and the shell keeps that one line in `<app config dir>/theme`, beside
// grants.json and outside every webview fs scope. At the next start the window and the webview get that background
// before the window is shown.
//
// The line is the Theme setting's word and the background of the dark and of the light App Theme picked
// (plans/app-themes §6.3): `system #221a13 #ffede0`. Colours, never theme ids, so a new App Theme changes nothing here.
// A file written before App Themes holds the word alone and starts in Querithm Dark's or Light's background.
//
// The file is only a copy: missing, unreadable or holding anything else, the window starts dark (the setting's
// default), and the webview corrects the colour as soon as it paints.
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, PoisonError};

use tauri::utils::config::Color;
use tauri::{AppHandle, Manager, Runtime, Theme, WebviewWindow};

use crate::shell_log;

/// Beside grants.json and window-state.json in the app config directory.
pub const FILE: &str = "theme";
/// The line is written here first and renamed over `FILE`, so a crash never leaves half a line.
const TMP_FILE: &str = "theme.tmp";

/// `--q-bg` of Querithm Dark and Light (src/theme/tokens.css): the colours of a one-word file and of no file;
/// scripts/__tests__/startScreen.test.ts keeps them in step.
pub const DARK_BG: (u8, u8, u8) = (0x0f, 0x11, 0x17);
pub const LIGHT_BG: (u8, u8, u8) = (0xff, 0xff, 0xff);

/// Longer than any line the webview writes ("system #rrggbb #rrggbb" is 22 bytes); a longer text is not ours.
const MAX_LEN: usize = 64;

/// The Theme setting as the webview names it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Mode {
    Dark,
    Light,
    System,
}

/// What the shell keeps: the setting and the window colour of each pick.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Kept {
    pub mode: Mode,
    pub dark: (u8, u8, u8),
    pub light: (u8, u8, u8),
}

/// `<dark|light|system>` (written before App Themes) or `<word> #rrggbb #rrggbb`; `None` for anything else (the file
/// and the command's argument are both untrusted).
pub fn parse(text: &str) -> Option<Kept> {
    if text.len() > MAX_LEN {
        return None;
    }
    let mut it = text.split_ascii_whitespace();
    let mode = match it.next()? {
        "dark" => Mode::Dark,
        "light" => Mode::Light,
        "system" => Mode::System,
        _ => return None,
    };
    match (it.next(), it.next(), it.next()) {
        (None, None, None) => Some(Kept { mode, dark: DARK_BG, light: LIGHT_BG }),
        (Some(dark), Some(light), None) => Some(Kept { mode, dark: rgb(dark)?, light: rgb(light)? }),
        _ => None,
    }
}

/// `#` and exactly six ASCII hex digits. Checked first: `from_str_radix` alone would also take a sign (`#+f0f0f`).
fn rgb(text: &str) -> Option<(u8, u8, u8)> {
    let hex = text.strip_prefix('#')?;
    if hex.len() != 6 || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let byte = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).ok();
    Some((byte(0)?, byte(2)?, byte(4)?))
}

fn word(mode: Mode) -> &'static str {
    match mode {
        Mode::Dark => "dark",
        Mode::Light => "light",
        Mode::System => "system",
    }
}

/// The line written for `kept`, made from the parsed values only.
fn serialize(kept: &Kept) -> String {
    let hex = |(r, g, b): (u8, u8, u8)| format!("#{r:02x}{g:02x}{b:02x}");
    format!("{} {} {}", word(kept.mode), hex(kept.dark), hex(kept.light))
}

/// The background the window starts with: nothing kept is the setting's default, Querithm Dark; "system" follows the OS.
pub fn background(kept: Option<Kept>, os_dark: bool) -> (u8, u8, u8) {
    match kept {
        None => DARK_BG,
        Some(k) => match k.mode {
            Mode::Dark => k.dark,
            Mode::Light => k.light,
            Mode::System if os_dark => k.dark,
            Mode::System => k.light,
        },
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
    let follows_os = matches!(kept, Some(Kept { mode: Mode::System, .. }));
    let os_dark = follows_os && !matches!(window.theme(), Ok(Theme::Light));
    let (r, g, b) = background(kept, os_dark);
    if let Err(e) = window.set_background_color(Some(Color(r, g, b, 255))) {
        shell_log::write("warn", &format!("window start colour not set: {e}"));
    }
}

/// Writes the kept line one call at a time, and the newest call to reach the shell wins. The webview sends one line at
/// a time but gives up on a call after 2 s (SHELL_KEEP_LIMIT_MS, src/app/theme.ts) and sends the next; the call it gave
/// up on may still be writing. The lock alone does not order the waiting calls, so each takes a ticket when it arrives
/// and a call older than the line already written writes nothing.
struct Keeper {
    next: AtomicU64,
    /// The ticket of the line in the file (0: none written in this run).
    written: Mutex<u64>,
}

impl Keeper {
    const fn new() -> Self {
        Self { next: AtomicU64::new(0), written: Mutex::new(0) }
    }

    /// This call's place in line: taken first, before anything that may wait.
    fn ticket(&self) -> u64 {
        self.next.fetch_add(1, Ordering::SeqCst) + 1
    }

    /// Writes `kept` into `dir` (a temporary file renamed over `FILE`); `false` when a newer call already wrote.
    fn keep(&self, ticket: u64, dir: &Path, kept: &Kept) -> std::io::Result<bool> {
        let mut written = self.written.lock().unwrap_or_else(PoisonError::into_inner);
        if *written > ticket {
            return Ok(false);
        }
        std::fs::create_dir_all(dir)?;
        let tmp = dir.join(TMP_FILE);
        std::fs::write(&tmp, serialize(kept))?;
        std::fs::rename(&tmp, dir.join(FILE))?;
        *written = ticket;
        Ok(true)
    }
}

static KEEPER: Keeper = Keeper::new();

/// The webview loaded the Theme setting (or the user changed it): kept for the next start.
#[tauri::command]
pub async fn app_theme_keep(app: AppHandle, theme: String) -> Result<(), String> {
    let ticket = KEEPER.ticket();
    let kept = parse(&theme).ok_or_else(|| "unknown theme".to_string())?;
    let dir = app.path().app_config_dir().map_err(|e| e.to_string())?;
    KEEPER.keep(ticket, &dir, &kept).map(|_| ()).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    const DARK_PICK: (u8, u8, u8) = (0x22, 0x1a, 0x13);
    const LIGHT_PICK: (u8, u8, u8) = (0xff, 0xed, 0xe0);

    #[test]
    fn a_word_alone_is_a_file_from_before_app_themes_with_querithm_colours() {
        let kept = |mode| Some(Kept { mode, dark: DARK_BG, light: LIGHT_BG });
        assert_eq!(parse("dark"), kept(Mode::Dark));
        assert_eq!(parse("light\n"), kept(Mode::Light));
        assert_eq!(parse(" system "), kept(Mode::System));
    }

    #[test]
    fn the_word_and_both_colours_are_read() {
        assert_eq!(parse("system #221a13 #ffede0"), Some(Kept { mode: Mode::System, dark: DARK_PICK, light: LIGHT_PICK }));
        assert_eq!(parse("dark #221A13 #FFEDE0\n"), Some(Kept { mode: Mode::Dark, dark: DARK_PICK, light: LIGHT_PICK }));
    }

    #[test]
    fn anything_else_is_not_read() {
        for other in [
            "",
            "Light",
            "blue",
            "light dark",
            "../grants.json",
            "\0",
            "dark #221a13",
            "dark #221a13 #ffede0 #000000",
            "dark 221a13 ffede0",
            "dark #221a1 #ffede0",
            "dark #221a133 #ffede0",
            "dark #22 1a13 #ffede0",
            "dark #22la13 #ffede0",
            "dark #+21a13 #ffede0",
            "dark #-21a13 #ffede0",
            "dark #２２1a13 #ffede0",
            "blue #221a13 #ffede0",
        ] {
            assert_eq!(parse(other), None, "{other:?}");
        }
        let long = format!("dark #221a13 #ffede0{}", " ".repeat(MAX_LEN));
        assert_eq!(parse(&long), None);
    }

    #[test]
    fn what_is_written_reads_back_as_what_was_parsed() {
        for mode in [Mode::Dark, Mode::Light, Mode::System] {
            for (dark, light) in [(DARK_BG, LIGHT_BG), (DARK_PICK, LIGHT_PICK), ((0, 0, 0), (0xff, 0xff, 0xff))] {
                let kept = Kept { mode, dark, light };
                assert_eq!(parse(&serialize(&kept)), Some(kept));
            }
        }
        assert_eq!(serialize(&parse("light #221A13 #FFEDE0").unwrap()), "light #221a13 #ffede0");
        // A file from before App Themes is written back with the colours it stands for.
        assert_eq!(serialize(&parse("system").unwrap()), "system #0f1117 #ffffff");
    }

    #[test]
    fn nothing_kept_starts_dark_like_the_default_setting() {
        assert_eq!(background(None, false), DARK_BG);
        assert_eq!(background(None, true), DARK_BG);
    }

    #[test]
    fn dark_and_light_show_their_pick_whatever_the_os_shows() {
        let kept = |mode| Some(Kept { mode, dark: DARK_PICK, light: LIGHT_PICK });
        assert_eq!(background(kept(Mode::Dark), false), DARK_PICK);
        assert_eq!(background(kept(Mode::Dark), true), DARK_PICK);
        assert_eq!(background(kept(Mode::Light), true), LIGHT_PICK);
        assert_eq!(background(kept(Mode::Light), false), LIGHT_PICK);
    }

    #[test]
    fn system_shows_the_pick_of_the_os_theme() {
        let kept = Some(Kept { mode: Mode::System, dark: DARK_PICK, light: LIGHT_PICK });
        assert_eq!(background(kept, true), DARK_PICK);
        assert_eq!(background(kept, false), LIGHT_PICK);
        assert_eq!(background(parse("system"), true), DARK_BG);
        assert_eq!(background(parse("system"), false), LIGHT_BG);
    }

    #[test]
    fn a_missing_or_damaged_file_keeps_nothing() {
        let dir = std::env::temp_dir().join(format!("querithm-theme-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join(FILE);
        let _ = std::fs::remove_file(&file);
        assert_eq!(read(&file), None);
        std::fs::write(&file, [0xff, 0xfe, 0x00]).unwrap();
        assert_eq!(read(&file), None);
        std::fs::write(&file, "light").unwrap();
        assert_eq!(read(&file), Some(Kept { mode: Mode::Light, dark: DARK_BG, light: LIGHT_BG }));
        std::fs::write(&file, "light #221a13 #ffede0").unwrap();
        assert_eq!(read(&file), Some(Kept { mode: Mode::Light, dark: DARK_PICK, light: LIGHT_PICK }));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn the_newest_call_wins_and_no_temporary_file_is_left() {
        // A directory of its own: the test above uses the process id alone and the tests run side by side.
        let dir = std::env::temp_dir().join(format!("querithm-theme-keep-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let keeper = Keeper::new();
        let older = keeper.ticket();
        let newer = keeper.ticket();
        assert!(older < newer);
        let light = Kept { mode: Mode::Light, dark: DARK_PICK, light: LIGHT_PICK };
        let dark = Kept { mode: Mode::Dark, dark: DARK_BG, light: LIGHT_BG };
        // The newer call writes first (the older one was slow); the older one, finishing late, writes nothing.
        assert!(keeper.keep(newer, &dir, &light).unwrap());
        assert!(!keeper.keep(older, &dir, &dark).unwrap());
        assert_eq!(read(&dir.join(FILE)), Some(light));
        assert!(!dir.join(TMP_FILE).exists());
        // A later call replaces the line whole.
        let latest = keeper.ticket();
        assert!(keeper.keep(latest, &dir, &dark).unwrap());
        assert_eq!(read(&dir.join(FILE)), Some(dark));
        assert_eq!(std::fs::read_to_string(dir.join(FILE)).unwrap(), "dark #0f1117 #ffffff");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
