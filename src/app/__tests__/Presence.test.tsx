// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { PresenceList } from "../Presence";
import { Modal } from "../Modal";
import "./domPolyfills";

afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); });

describe("dialog presence", () => {
  it("makes closing dialogs inert, restores focus, and ignores keyboard confirmation during exit", () => {
    vi.useFakeTimers();
    const opener = document.createElement("button");
    document.body.append(opener);
    opener.focus();
    const item = {};
    const enter = vi.fn();
    const content = () => <Modal title="Confirm" onClose={() => {}} onEnter={enter}>body</Modal>;
    const { rerender, container } = render(<PresenceList items={[item]}>{content}</PresenceList>);
    expect(document.activeElement).toBe(screen.getByRole("dialog"));
    rerender(<PresenceList items={[]}>{content}</PresenceList>);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(container.querySelector("[inert][data-q-exiting]")).not.toBeNull();
    expect(document.activeElement).toBe(opener);
    fireEvent.keyDown(window, { key: "Enter" });
    expect(enter).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(150));
    expect(container.querySelector("[data-q-exiting]")).toBeNull();
    opener.remove();
  });

  it("preserves the remaining dialog instance when another is removed and supports rapid reopen", () => {
    vi.useFakeTimers();
    const a = { title: "a" }, b = { title: "b" };
    const content = (item: typeof a) => <Modal title={item.title} onClose={() => {}}><input aria-label={item.title} defaultValue="original" /></Modal>;
    const { rerender } = render(<PresenceList items={[a, b]}>{content}</PresenceList>);
    fireEvent.change(screen.getByRole("textbox", { name: "b" }), { target: { value: "edited" } });
    rerender(<PresenceList items={[b]}>{content}</PresenceList>);
    expect((screen.getByRole("textbox", { name: "b" }) as HTMLInputElement).value).toBe("edited");
    rerender(<PresenceList items={[a, b]}>{content}</PresenceList>);
    act(() => vi.advanceTimersByTime(150));
    expect(screen.getAllByRole("dialog")).toHaveLength(2);
    expect((screen.getByRole("textbox", { name: "b" }) as HTMLInputElement).value).toBe("edited");
  });

  it("skips the exit wait when reduced motion is requested", () => {
    vi.useFakeTimers();
    vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: true })));
    const item = {};
    const { rerender, container } = render(<PresenceList items={[item]}>{() => <div>content</div>}</PresenceList>);
    rerender(<PresenceList items={[]}>{() => <div>content</div>}</PresenceList>);
    act(() => vi.advanceTimersByTime(0));
    expect(container.textContent).toBe("");
    vi.unstubAllGlobals();
  });
});
