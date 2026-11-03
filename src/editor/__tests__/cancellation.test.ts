import { describe, it, expect, vi } from "vitest";
import { ignoreMonacoCancellations, isMonacoCancellation } from "../cancellation";

describe("Monaco cancellations", () => {
  it("recognises only Monaco's CancellationError", () => {
    const canceled = Object.assign(new Error("Canceled"), { name: "Canceled" });
    expect(isMonacoCancellation(canceled)).toBe(true);
    expect(isMonacoCancellation(new Error("Canceled"))).toBe(false); // name is "Error"
    expect(isMonacoCancellation(Object.assign(new Error("boom"), { name: "Canceled" }))).toBe(false);
    expect(isMonacoCancellation("Canceled")).toBe(false);
    expect(isMonacoCancellation(null)).toBe(false);
  });

  it("swallows those rejections and leaves every other one alone", () => {
    let handler: ((e: { reason: unknown; preventDefault: () => void }) => void) | undefined;
    ignoreMonacoCancellations({ addEventListener: (_: string, h: never) => (handler = h) } as never);
    const ok = { reason: Object.assign(new Error("Canceled"), { name: "Canceled" }), preventDefault: vi.fn() };
    handler!(ok);
    expect(ok.preventDefault).toHaveBeenCalled();
    const real = { reason: new TypeError("x is undefined"), preventDefault: vi.fn() };
    handler!(real);
    expect(real.preventDefault).not.toHaveBeenCalled();
  });
});
