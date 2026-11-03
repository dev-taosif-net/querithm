// Quitting (H4, §4): a close request on the main window (title-bar X, Alt+F4, the OS quitting the app) is held until the
// webview answers, so it can ask about open transactions, staged grid edits and running queries, and write what still
// waits on its debounce. The window still closes when the webview cannot answer: no acknowledgement within ACK_LIMIT
// (hung, crashed or still loading). A second request while the webview shows no prompt is answered "close" by the
// webview itself. An update install restarts the app without asking (the update dialog already listed what is lost).
// No window permission is needed: the shell holds and closes the window, the webview only answers (§13 rule 6).
use std::sync::Mutex;
use std::time::{Duration, Instant};

use tauri::{AppHandle, Emitter, Manager, State};

pub const MAIN: &str = "main";
/// The event the webview listens for (src/app/quit.ts).
pub const EVENT: &str = "app://close-requested";
/// How long the webview has to acknowledge a close request before the window closes without it.
pub const ACK_LIMIT: Duration = Duration::from_secs(3);

/// What to do with one close request.
#[derive(Debug, PartialEq, Eq)]
pub enum Decision {
    /// Let the window close.
    Close,
    /// Hold the close and ask the webview; the number identifies the request for its acknowledgement timer.
    Ask(u64),
    /// Hold the close: an earlier request is not acknowledged yet, and its timer closes the window if it never is.
    Wait,
}

struct Pending {
    seq: u64,
    acked: bool,
    at: Instant,
}

#[derive(Default)]
struct Inner {
    allowed: bool,
    seq: u64,
    pending: Option<Pending>,
}

/// The close decision, kept apart from the window so it is testable.
#[derive(Default)]
pub struct CloseGuard(Mutex<Inner>);

impl CloseGuard {
    pub fn request(&self, now: Instant) -> Decision {
        let mut s = self.0.lock().unwrap();
        if s.allowed {
            return Decision::Close;
        }
        if let Some(p) = &s.pending {
            if !p.acked {
                // A second click before the webview answered: its timer decides, unless that time has already passed.
                if now.duration_since(p.at) < ACK_LIMIT {
                    return Decision::Wait;
                }
                s.allowed = true;
                return Decision::Close;
            }
        }
        // No request yet, or one the webview is handling: ask (again); the webview knows whether a prompt is showing.
        s.seq += 1;
        let seq = s.seq;
        s.pending = Some(Pending { seq, acked: false, at: now });
        Decision::Ask(seq)
    }

    /// The webview received the request.
    pub fn ack(&self) {
        if let Some(p) = self.0.lock().unwrap().pending.as_mut() {
            p.acked = true;
        }
    }

    /// The webview's answer; true when the window closes now.
    pub fn answer(&self, close: bool) -> bool {
        let mut s = self.0.lock().unwrap();
        s.pending = None;
        if close {
            s.allowed = true;
        }
        close
    }

    /// The acknowledgement timer of request `seq` ran out: true when it was never acknowledged (the window closes).
    pub fn expired(&self, seq: u64) -> bool {
        let mut s = self.0.lock().unwrap();
        match &s.pending {
            Some(p) if p.seq == seq && !p.acked => {
                s.allowed = true;
                true
            }
            _ => false,
        }
    }

    /// The app closes for a reason decided elsewhere (an update install restarting it): nothing waits for the webview.
    pub fn allow(&self) {
        self.0.lock().unwrap().allowed = true;
    }
}

/// A close request on the main window, or the OS asking the app to quit while it is open. `prevent` holds it.
pub fn on_request(app: &AppHandle, prevent: impl FnOnce()) {
    let guard = app.state::<CloseGuard>();
    match guard.request(Instant::now()) {
        Decision::Close => {}
        Decision::Wait => prevent(),
        Decision::Ask(seq) => {
            prevent();
            if app.emit(EVENT, ()).is_err() {
                // Nobody to ask: close as if the timer had run out.
                if guard.expired(seq) {
                    close_now(app);
                }
                return;
            }
            let app = app.clone();
            tauri::async_runtime::spawn(async move {
                tokio::time::sleep(ACK_LIMIT).await;
                if app.state::<CloseGuard>().expired(seq) {
                    close_now(&app);
                }
            });
        }
    }
}

/// Closes the main window without another close request (the guard already allowed it); the app then exits (§4 step 5).
fn close_now(app: &AppHandle) {
    let handle = app.clone();
    let _ = app.run_on_main_thread(move || match handle.get_webview_window(MAIN) {
        Some(w) => {
            let _ = w.destroy();
        }
        None => handle.exit(0),
    });
}

#[tauri::command]
pub fn app_close_ack(guard: State<'_, CloseGuard>) {
    guard.ack();
}

#[tauri::command]
pub fn app_close_answer(app: AppHandle, guard: State<'_, CloseGuard>, close: bool) {
    if guard.answer(close) {
        close_now(&app);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_first_request_asks_the_webview_and_holds_the_close() {
        let g = CloseGuard::default();
        assert_eq!(g.request(Instant::now()), Decision::Ask(1));
    }

    #[test]
    fn the_webview_answering_close_lets_the_next_request_through() {
        let g = CloseGuard::default();
        let now = Instant::now();
        let Decision::Ask(seq) = g.request(now) else { panic!("expected Ask") };
        g.ack();
        assert!(g.answer(true));
        assert!(!g.expired(seq));
        assert_eq!(g.request(now), Decision::Close);
    }

    #[test]
    fn keeping_the_app_open_asks_again_on_the_next_request() {
        let g = CloseGuard::default();
        let now = Instant::now();
        g.request(now);
        g.ack();
        assert!(!g.answer(false));
        assert_eq!(g.request(now), Decision::Ask(2));
    }

    #[test]
    fn a_webview_that_never_acknowledges_gets_the_window_closed_by_the_timer() {
        let g = CloseGuard::default();
        let Decision::Ask(seq) = g.request(Instant::now()) else { panic!("expected Ask") };
        assert!(g.expired(seq));
        assert_eq!(g.request(Instant::now()), Decision::Close);
    }

    #[test]
    fn an_acknowledged_request_is_not_closed_by_its_timer_while_the_user_answers() {
        let g = CloseGuard::default();
        let Decision::Ask(seq) = g.request(Instant::now()) else { panic!("expected Ask") };
        g.ack();
        assert!(!g.expired(seq));
    }

    #[test]
    fn a_second_click_before_the_acknowledgement_waits_and_one_after_the_limit_closes() {
        let g = CloseGuard::default();
        let t0 = Instant::now();
        g.request(t0);
        assert_eq!(g.request(t0 + Duration::from_millis(200)), Decision::Wait);
        assert_eq!(g.request(t0 + ACK_LIMIT), Decision::Close);
    }

    #[test]
    fn a_second_request_while_the_webview_handles_the_first_asks_it_again_with_a_new_timer() {
        let g = CloseGuard::default();
        let t0 = Instant::now();
        let Decision::Ask(first) = g.request(t0) else { panic!("expected Ask") };
        g.ack();
        let Decision::Ask(second) = g.request(t0 + Duration::from_secs(30)) else { panic!("expected Ask") };
        assert_ne!(first, second);
        assert!(!g.expired(first)); // the old timer no longer decides
        assert!(g.expired(second)); // a webview hung since its first answer: the window closes
    }

    #[test]
    fn an_update_restart_never_waits_for_the_webview() {
        let g = CloseGuard::default();
        g.allow();
        assert_eq!(g.request(Instant::now()), Decision::Close);
    }
}
