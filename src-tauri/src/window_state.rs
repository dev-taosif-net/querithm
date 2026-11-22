// Window placement across restarts (§16): the main window opens where it was closed — its place, its size, maximized
// or fullscreen. tauri-plugin-window-state keeps the record, driven from Rust only: the webview holds no window-state
// permission (§13 rule 6) and `withGlobalTauri` is off, so the plugin's script is never injected. The file is
// `<app config dir>/window-state.json`, beside grants.json and outside every webview fs scope; it holds window
// geometry only, nothing about connections or scripts.
//
// The window is created hidden (tauri.conf.json `visible: false`) so it never shows at the default place first and
// jumps: `restore` puts back its normal rectangle, pulls it onto a screen that still exists (`fit`), maximizes it or
// makes it fullscreen again, and shows it. It always shows: a missing or unreadable file, or a restore that fails,
// leaves the default — 1440 × 900, centred on the primary screen.
//
// Saving: the plugin follows moves and resizes in memory and writes the file at `RunEvent::Exit`. Two ways out never
// reach that with the window alive, so they write it first: the close guard destroying the window (close.rs) and a
// Windows update install, which exits the process itself (updater.rs). A crash keeps the last file written. With two
// instances open (M23) the one closed last wins.
use std::sync::mpsc;
use std::time::Duration;

use tauri::{AppHandle, Monitor, PhysicalPosition, PhysicalSize, Runtime, WebviewWindow};
use tauri_plugin_window_state::{AppHandleExt, StateFlags, WindowExt};

use crate::{close, shell_log};

/// Beside grants.json in the app config directory.
pub const FILE: &str = "window-state.json";

/// What is kept. Not visibility (the window always opens shown) and not decorations (fixed in tauri.conf.json).
pub fn flags() -> StateFlags {
    StateFlags::SIZE | StateFlags::POSITION | StateFlags::MAXIMIZED | StateFlags::FULLSCREEN
}

pub fn plugin<R: Runtime>() -> tauri::plugin::TauriPlugin<R> {
    tauri_plugin_window_state::Builder::new()
        .with_state_flags(flags())
        .with_filename(FILE)
        // `restore` restores the main window itself, in order and before it is shown; the plugin's own restore runs
        // later on the event loop, after the window would already be visible. The plugin still follows its moves.
        .skip_initial_state(close::MAIN)
        .build()
}

/// Puts the main window back where it was closed and shows it. Never leaves it hidden.
pub fn restore<R: Runtime>(window: &WebviewWindow<R>) {
    // The normal rectangle first, so that it is also where Restore Down returns to from a maximized window.
    if let Err(e) = window.restore_state(StateFlags::SIZE | StateFlags::POSITION) {
        shell_log::write("warn", &format!("window place not restored: {e}"));
    }
    if let Err(e) = keep_on_screen(window) {
        shell_log::write("warn", &format!("window not checked against the screens: {e}"));
    }
    if let Err(e) = window.restore_state(StateFlags::MAXIMIZED | StateFlags::FULLSCREEN) {
        shell_log::write("warn", &format!("window maximized / fullscreen state not restored: {e}"));
    }
    let _ = window.show();
    let _ = window.set_focus();
}

/// How long `save_from_worker` waits for the main thread before the caller goes on without the save.
const SAVE_LIMIT: Duration = Duration::from_secs(1);

/// Writes the window's current place now. Never fails the caller: the app still closes. Main thread only: the plugin
/// holds its record's lock while it reads the window, and a window read from another thread waits for the main thread,
/// which a move or resize arriving meanwhile has waiting for that lock.
pub fn save<R: Runtime>(app: &AppHandle<R>) {
    if let Err(e) = app.save_window_state(flags()) {
        shell_log::write("warn", &format!("window place not saved: {e}"));
    }
}

/// `save` from any other thread: runs it on the main thread and waits for it at most SAVE_LIMIT.
pub fn save_from_worker<R: Runtime>(app: &AppHandle<R>) {
    let (done, wait) = mpsc::channel();
    let handle = app.clone();
    let queued = app.run_on_main_thread(move || {
        save(&handle);
        let _ = done.send(());
    });
    if queued.is_ok() && wait.recv_timeout(SAVE_LIMIT).is_err() {
        shell_log::write("warn", "window place not saved: the main thread did not answer in time");
    }
}

/// The plugin puts a window back on its saved place only when a corner of it is on a screen, and always at its saved
/// size: after a monitor is unplugged or the resolution drops, the window can open larger than the screen or with its
/// title bar out of reach. `fit` decides; this applies it.
fn keep_on_screen<R: Runtime>(w: &WebviewWindow<R>) -> tauri::Result<()> {
    let screens: Vec<Rect> = w.available_monitors()?.iter().map(work_area).collect();
    let primary = w.primary_monitor()?.as_ref().map(work_area);
    let (pos, outer, inner) = (w.outer_position()?, w.outer_size()?, w.inner_size()?);
    let now = Rect { x: pos.x, y: pos.y, w: outer.width, h: outer.height };
    if let Some(to) = fit(now, (inner.width, inner.height), &screens, primary) {
        // set_size takes the client size: the frame keeps its own width and height.
        let frame = (outer.width.saturating_sub(inner.width), outer.height.saturating_sub(inner.height));
        w.set_size(PhysicalSize::new(to.w.saturating_sub(frame.0).max(1), to.h.saturating_sub(frame.1).max(1)))?;
        w.set_position(PhysicalPosition::new(to.x, to.y))?;
    }
    Ok(())
}

fn work_area(m: &Monitor) -> Rect {
    let a = m.work_area();
    Rect { x: a.position.x, y: a.position.y, w: a.size.width, h: a.size.height }
}

/// A rectangle in physical pixels.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: u32,
    pub h: u32,
}

impl Rect {
    fn contains(&self, px: i64, py: i64) -> bool {
        let (x, y) = (i64::from(self.x), i64::from(self.y));
        px >= x && px < x + i64::from(self.w) && py >= y && py < y + i64::from(self.h)
    }
}

/// How far below the window's top edge the point that must stay on a screen sits: inside the title bar at any scale.
const GRAB_DEPTH: i64 = 8;

/// Where a restored window goes when its saved place no longer suits the screens; `None` leaves it where it is.
/// `outer` is the window with its frame, `client` its content size, `screens` the monitors' work areas (taskbar and
/// dock left out) and `primary` the primary monitor's.
/// - The middle of its title bar is on a screen and its content fits that screen: it stays.
/// - The middle of its title bar is on a screen but the window is larger: it shrinks to the screen and moves into it.
/// - The middle of its title bar is on no screen: it goes to the middle of the primary screen, shrunk to fit.
pub fn fit(outer: Rect, client: (u32, u32), screens: &[Rect], primary: Option<Rect>) -> Option<Rect> {
    let grab = (i64::from(outer.x) + i64::from(outer.w) / 2, i64::from(outer.y) + GRAB_DEPTH);
    if let Some(s) = screens.iter().find(|s| s.contains(grab.0, grab.1)) {
        if client.0 <= s.w && client.1 <= s.h {
            return None;
        }
        let (w, h) = (outer.w.min(s.w), outer.h.min(s.h));
        return Some(Rect { x: slide_into(outer.x, s.x, s.w, w), y: slide_into(outer.y, s.y, s.h, h), w, h });
    }
    let s = primary.or_else(|| screens.first().copied())?;
    let (w, h) = (outer.w.min(s.w), outer.h.min(s.h));
    let centre = |lo: i32, span: u32, len: u32| (i64::from(lo) + i64::from((span - len) / 2)) as i32;
    Some(Rect { x: centre(s.x, s.w, w), y: centre(s.y, s.h, h), w, h })
}

/// `start` moved as little as needed for `[start, start + len)` to lie inside `[lo, lo + span)`; `len` ≤ `span`.
fn slide_into(start: i32, lo: i32, span: u32, len: u32) -> i32 {
    let hi = i64::from(lo) + i64::from(span - len);
    i64::from(start).clamp(i64::from(lo), hi) as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A 1920 × 1080 primary screen with a 48 px taskbar, and a 2560 × 1440 screen to its right.
    const LAPTOP: Rect = Rect { x: 0, y: 0, w: 1920, h: 1032 };
    const RIGHT: Rect = Rect { x: 1920, y: 0, w: 2560, h: 1392 };

    fn win(x: i32, y: i32, w: u32, h: u32) -> Rect {
        Rect { x, y, w, h }
    }

    /// The client area of a window with a 16 px wide, 39 px high frame (Windows at 100 %, title bar included).
    fn client(r: Rect) -> (u32, u32) {
        (r.w - 16, r.h - 39)
    }

    #[test]
    fn a_window_on_a_screen_stays_where_it_was_closed() {
        let w = win(200, 120, 1456, 939);
        assert_eq!(fit(w, client(w), &[LAPTOP, RIGHT], Some(LAPTOP)), None);
    }

    #[test]
    fn a_window_on_the_second_screen_stays_there_while_that_screen_is_connected() {
        let w = win(2400, 300, 1456, 939);
        assert_eq!(fit(w, client(w), &[LAPTOP, RIGHT], Some(LAPTOP)), None);
    }

    #[test]
    fn a_window_partly_off_the_edge_stays_while_its_title_bar_can_be_grabbed() {
        // The user left it hanging off the left edge: still reachable, so it is theirs to keep.
        let w = win(-600, 100, 1456, 939);
        assert_eq!(fit(w, client(w), &[LAPTOP], Some(LAPTOP)), None);
    }

    #[test]
    fn a_window_snapped_to_the_full_screen_height_is_left_alone() {
        // Windows' invisible resize borders make the outer rectangle overhang the work area; the content still fits.
        let w = win(953, 0, 974, 1039);
        assert_eq!(fit(w, client(w), &[LAPTOP], Some(LAPTOP)), None);
    }

    #[test]
    fn a_window_closed_on_a_screen_that_is_gone_opens_centred_on_the_primary_screen() {
        let w = win(2400, 300, 1456, 939);
        assert_eq!(fit(w, client(w), &[LAPTOP], Some(LAPTOP)), Some(win(232, 46, 1456, 939)));
    }

    #[test]
    fn a_window_larger_than_the_only_screen_left_shrinks_to_it() {
        // Closed full-height on the big screen, which is now unplugged: the laptop's screen is all there is.
        let w = win(2000, 0, 2400, 1380);
        assert_eq!(fit(w, client(w), &[LAPTOP], Some(LAPTOP)), Some(LAPTOP));
    }

    #[test]
    fn a_window_larger_than_its_screen_after_a_resolution_change_shrinks_and_moves_into_it() {
        let small = Rect { x: 0, y: 0, w: 1366, h: 728 };
        let w = win(100, 50, 1456, 939);
        assert_eq!(fit(w, client(w), &[small], Some(small)), Some(win(0, 0, 1366, 728)));
    }

    #[test]
    fn only_the_dimension_that_overflows_shrinks() {
        let small = Rect { x: 0, y: 0, w: 1366, h: 728 };
        let w = win(300, 20, 1000, 939);
        assert_eq!(fit(w, client(w), &[small], Some(small)), Some(win(300, 0, 1000, 728)));
    }

    #[test]
    fn a_title_bar_above_the_top_of_every_screen_brings_the_window_back() {
        let w = win(200, -500, 1456, 939);
        assert_eq!(fit(w, client(w), &[LAPTOP, RIGHT], Some(LAPTOP)), Some(win(232, 46, 1456, 939)));
    }

    #[test]
    fn a_screen_left_of_the_primary_with_negative_coordinates_counts() {
        let left = Rect { x: -1920, y: 0, w: 1920, h: 1032 };
        let w = win(-1700, 100, 1456, 939);
        assert_eq!(fit(w, client(w), &[left, LAPTOP], Some(LAPTOP)), None);
    }

    #[test]
    fn without_a_primary_screen_the_first_screen_is_used() {
        let w = win(9000, 9000, 800, 600);
        assert_eq!(fit(w, client(w), &[RIGHT], None), Some(win(2800, 396, 800, 600)));
    }

    #[test]
    fn with_no_screens_reported_the_window_is_left_alone() {
        let w = win(9000, 9000, 800, 600);
        assert_eq!(fit(w, client(w), &[], None), None);
    }

    #[test]
    fn extreme_saved_coordinates_do_not_overflow() {
        let w = win(i32::MAX - 10, i32::MAX - 10, u32::MAX, u32::MAX);
        assert_eq!(fit(w, (u32::MAX, u32::MAX), &[LAPTOP], Some(LAPTOP)), Some(LAPTOP));
    }
}
