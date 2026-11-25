import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./theme/tokens.css";
import { App } from "./app/App";
import { ErrorBoundary, errorReport, reportRenderError } from "./app/ErrorBoundary";
import { applyCachedUiScale } from "./app/uiScale";
import { installAppThemeStyles } from "./theme/appThemes";

// W8: the interface scale the window last had, before anything renders (the setting corrects it once it has loaded).
void applyCachedUiScale();
// The seven new App Themes' token blocks (plans/app-themes §2.1), before the first render; if this fails the document
// shows Querithm Dark / Light and the editor agrees (resolveDocumentAppTheme).
if (!installAppThemeStyles()) console.error("App Theme styles could not be installed; showing Querithm Dark / Light.");

// M17: the root boundary catches what no panel boundary did; an error even it cannot render is still logged.
createRoot(document.getElementById("root")!, {
  onUncaughtError: (error, info) => {
    console.error(error);
    reportRenderError(errorReport("root (uncaught)", error, info.componentStack));
  },
}).render(
  <StrictMode>
    <ErrorBoundary name="root" variant="root">
      <App />
    </ErrorBoundary>
  </StrictMode>,
);
