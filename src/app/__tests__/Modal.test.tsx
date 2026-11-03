// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactElement } from "react";
import "./domPolyfills";
import { Modal } from "../Modal";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const keydowns = (spy: ReturnType<typeof vi.spyOn>) => spy.mock.calls.filter((c) => c[0] === "keydown").length;

describe("Modal keyboard", () => {
  // A dialog re-renders with a new onClose whenever its state changes (the import wizard after its job starts).
  // Re-subscribing in a passive effect left a window where a key still ran the previous render's onClose.
  it("keeps one keydown listener across re-renders and always runs the latest handlers", () => {
    const add = vi.spyOn(window, "addEventListener");
    const first = vi.fn();
    const { rerender } = render(<Modal title="t" onClose={first}>body</Modal>);
    const latest = vi.fn();
    const enter = vi.fn();
    rerender(<Modal title="t" onClose={latest} onEnter={enter}>body</Modal>);
    rerender(<Modal title="t" onClose={latest} onEnter={enter}>body</Modal>);
    expect(keydowns(add)).toBe(1);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(first).not.toHaveBeenCalled();
    expect(latest).toHaveBeenCalledTimes(1);
    fireEvent.keyDown(window, { key: "Enter" });
    expect(enter).toHaveBeenCalledTimes(1);
  });

  it("Enter does nothing once onEnter is removed", () => {
    const enter = vi.fn();
    const { rerender } = render(<Modal title="t" onClose={() => {}} onEnter={enter}>body</Modal>);
    rerender(<Modal title="t" onClose={() => {}}>body</Modal>);
    fireEvent.keyDown(window, { key: "Enter" });
    expect(enter).not.toHaveBeenCalled();
  });

  it("only the topmost dialog handles keys, and unmounting removes the listener", () => {
    const under = vi.fn();
    const over = vi.fn();
    const remove = vi.spyOn(window, "removeEventListener");
    render(<Modal title="under" onClose={under}>a</Modal>);
    const top = render(<Modal title="over" onClose={over}>b</Modal>);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(over).toHaveBeenCalledTimes(1);
    expect(under).not.toHaveBeenCalled();
    top.unmount();
    expect(keydowns(remove)).toBe(1);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(under).toHaveBeenCalledTimes(1);
  });
});

// M19: Enter ran onEnter whatever had focus, so Enter on a focused Cancel confirmed, and a danger dialog opened on
// itself, so Enter right after it opened confirmed too.
describe("Modal Enter (M19)", () => {
  it("Enter on a focused button activates that button, never onEnter", async () => {
    const enter = vi.fn();
    const cancel = vi.fn();
    const { getByText } = render(<Modal title="t" onClose={() => {}} onEnter={enter} footer={<button onClick={cancel}>Cancel</button>}>body</Modal>);
    getByText("Cancel").focus();
    await userEvent.setup().keyboard("{Enter}");
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(enter).not.toHaveBeenCalled();
  });

  const controls: [string, ReactElement][] = [
    ["a link", <a href="https://example.com" aria-label="x">x</a>],
    ["a select", <select aria-label="x"><option>a</option></select>],
    ["a checkbox", <input type="checkbox" aria-label="x" />],
    ...["button", "menuitem", "option", "checkbox", "radio", "switch", "tab"].map((role): [string, ReactElement] => [`role=${role}`, <div role={role} tabIndex={0} aria-label="x" />]),
  ];
  it.each(controls)("Enter on %s is left to that control", (_, control) => {
    const enter = vi.fn();
    const { getByLabelText, getByRole } = render(<Modal title="t" onClose={() => {}} onEnter={enter}>{control}</Modal>);
    const el = getByLabelText("x");
    el.focus();
    expect(fireEvent.keyDown(el, { key: "Enter" })).toBe(true); // not prevented: the control's own activation runs
    expect(enter).not.toHaveBeenCalled();
    fireEvent.keyDown(getByRole("dialog"), { key: "Enter" });
    expect(enter).toHaveBeenCalledTimes(1);
  });

  it("a non-danger dialog still submits on Enter in a text field", async () => {
    const enter = vi.fn();
    const { getByLabelText } = render(<Modal title="t" onClose={() => {}} onEnter={enter}><input aria-label="name" autoFocus /></Modal>);
    await userEvent.setup().type(getByLabelText("name"), "abc{Enter}");
    expect(enter).toHaveBeenCalledTimes(1);
  });

  it("a danger dialog is an alertdialog that opens on its cancel control, so Enter right after opening does not confirm", async () => {
    const enter = vi.fn();
    const cancel = vi.fn();
    const { getByRole, getByText } = render(
      <Modal title="Delete?" danger onClose={() => {}} onEnter={enter} footer={<><button data-modal-cancel onClick={cancel}>Cancel</button><button>Delete</button></>}>body</Modal>,
    );
    expect(getByRole("alertdialog", { name: "Delete?" }).getAttribute("aria-modal")).toBe("true");
    expect(document.activeElement).toBe(getByText("Cancel"));
    await userEvent.setup().keyboard("{Enter}");
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(enter).not.toHaveBeenCalled();
  });

  it("an alert dialog confirms on Enter only from a field the user typed in, never from the dialog itself", async () => {
    const enter = vi.fn();
    const { getByRole, getByLabelText, rerender } = render(<Modal title="t" alert onClose={() => {}} onEnter={enter}>body</Modal>);
    expect(document.activeElement).toBe(getByRole("alertdialog")); // no cancel control: the dialog itself
    fireEvent.keyDown(getByRole("alertdialog"), { key: "Enter" });
    fireEvent.keyDown(window, { key: "Enter" });
    expect(enter).not.toHaveBeenCalled();
    rerender(<Modal title="t" alert onClose={() => {}} onEnter={enter}><input aria-label="value" /></Modal>);
    await userEvent.setup().type(getByLabelText("value"), "5{Enter}");
    expect(enter).toHaveBeenCalledTimes(1);
  });

  it("a dialog that is not an alert keeps role=dialog and focuses itself", () => {
    const { getByRole } = render(<Modal title="t" onClose={() => {}} footer={<button data-modal-cancel>Cancel</button>}>body</Modal>);
    expect(document.activeElement).toBe(getByRole("dialog"));
  });
});

describe("Modal title", () => {
  // The title used `uppercase`: "Rename @orderId" read "RENAME @ORDERID", not the identifier the user wrote.
  it.each([false, true])("shows identifiers exactly as written, with no text-transform (danger: %s)", (danger) => {
    const { container } = render(<Modal title="Rename @orderId" danger={danger} onClose={() => {}}>body</Modal>);
    const title = container.querySelector("[data-modal-title]") as HTMLElement;
    expect(title.textContent).toBe("Rename @orderId");
    expect(title.className).not.toMatch(/\b(uppercase|lowercase|capitalize)\b/);
  });
});

describe("Modal as a dialog (WAI-ARIA)", () => {
  it("is a modal dialog named by its title and described by its subtitle", () => {
    const { getByRole } = render(<Modal title="Settings" subtitle="Changes apply at once" onClose={() => {}}>body</Modal>);
    const d = getByRole("dialog", { name: "Settings" });
    expect(d.getAttribute("aria-modal")).toBe("true");
    expect(d.getAttribute("aria-describedby") && document.getElementById(d.getAttribute("aria-describedby")!)!.textContent).toBe("Changes apply at once");
  });

  it("moves focus into the dialog, but leaves it on a field the dialog focused itself", () => {
    const a = render(<Modal title="a" onClose={() => {}}><button>x</button></Modal>);
    expect(document.activeElement).toBe(a.getByRole("dialog"));
    a.unmount();
    const b = render(<Modal title="b" onClose={() => {}}><input aria-label="name" autoFocus /></Modal>);
    expect(document.activeElement).toBe(b.getByLabelText("name"));
  });

  it("Tab and Shift+Tab wrap inside the dialog", () => {
    const { getByText } = render(<Modal title="t" onClose={() => {}} footer={<button>Done</button>}><button>First</button><button disabled>Off</button></Modal>);
    const first = getByText("First");
    const done = getByText("Done");
    done.focus();
    fireEvent.keyDown(done, { key: "Tab" });
    expect(document.activeElement).toBe(first);
    fireEvent.keyDown(first, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(done);
  });

  it("Tab from the dialog itself or from outside goes to the first stop; Tab inside an editor is left to the editor", () => {
    const { getByText, getByRole, getByLabelText } = render(
      <Modal title="t" onClose={() => {}}><button>First</button><div className="monaco-editor"><textarea aria-label="code" /></div></Modal>,
    );
    fireEvent.keyDown(getByRole("dialog"), { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(getByLabelText("code"));
    fireEvent.keyDown(document.body, { key: "Tab" });
    expect(document.activeElement).toBe(getByText("First"));
    const code = getByLabelText("code");
    code.focus();
    expect(fireEvent.keyDown(code, { key: "Tab" })).toBe(true); // not prevented
    expect(document.activeElement).toBe(code);
  });

  it("closing returns focus to the opener, unless the dialog moved focus elsewhere", () => {
    const opener = document.createElement("button");
    const other = document.createElement("button");
    document.body.append(opener, other);
    opener.focus();
    const a = render(<Modal title="a" onClose={() => {}}><button>x</button></Modal>);
    expect(document.activeElement).not.toBe(opener);
    a.unmount();
    expect(document.activeElement).toBe(opener);
    const b = render(<Modal title="b" onClose={() => {}}><button>x</button></Modal>);
    other.focus(); // e.g. "Open in tab" focused the editor before closing
    b.unmount();
    expect(document.activeElement).toBe(other);
    opener.remove();
    other.remove();
  });

  it("onEscape runs first; the dialog closes only when it did not handle the key", () => {
    const close = vi.fn();
    let handled = true;
    render(<Modal title="t" onClose={close} onEscape={() => handled}>body</Modal>);
    fireEvent.keyDown(window, { key: "Escape" });
    expect(close).not.toHaveBeenCalled();
    handled = false;
    fireEvent.keyDown(window, { key: "Escape" });
    expect(close).toHaveBeenCalledTimes(1);
  });
});
