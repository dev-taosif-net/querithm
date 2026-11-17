// Vitest setup: Node 25+ defines its own globalThis.localStorage / sessionStorage (Web Storage), which read as undefined
// unless Node runs with --localstorage-file. They shadow jsdom's in jsdom-environment test files, so every
// localStorage call there threw. Where that happens, jsdom's own storage is put back. Node 24 (CI) has no such globals:
// nothing changes there, nor in node-environment files (no jsdom).
const dom = (globalThis as { jsdom?: { window: Window } }).jsdom;
if (dom) {
  for (const name of ["localStorage", "sessionStorage"] as const) {
    if ((globalThis as Record<string, unknown>)[name] === undefined) {
      Object.defineProperty(globalThis, name, { value: dom.window[name], configurable: true, writable: true });
    }
  }
}
