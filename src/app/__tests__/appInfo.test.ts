import { beforeEach, describe, expect, it, vi } from "vitest";

const h = vi.hoisted(() => ({ appInfo: vi.fn() }));
vi.mock("../../ipc/client", () => ({ shell: { appInfo: () => h.appInfo() } }));

import { getAppInfo, platformName, resetAppInfo } from "../appInfo";

const info = { version: "0.1.6", platform: "linux", bench: false, dataDir: "/home/u/.local/share/querithm" };

describe("appInfo", () => {
  beforeEach(() => {
    resetAppInfo();
    h.appInfo.mockReset();
  });

  it("asks the shell once and shares the answer", async () => {
    h.appInfo.mockResolvedValue(info);
    const [a, b] = await Promise.all([getAppInfo(), getAppInfo()]);
    expect(await getAppInfo()).toBe(a);
    expect(b).toBe(a);
    expect(h.appInfo).toHaveBeenCalledTimes(1);
  });

  it("a failed ask is not cached: the next caller asks again", async () => {
    h.appInfo.mockRejectedValueOnce(new Error("shell not ready")).mockResolvedValueOnce(info);
    await expect(getAppInfo()).rejects.toThrow("shell not ready");
    await expect(getAppInfo()).resolves.toEqual(info);
    expect(h.appInfo).toHaveBeenCalledTimes(2);
  });

  it("names platforms for people", () => {
    expect([platformName("linux"), platformName("windows"), platformName("macos"), platformName("freebsd"), platformName(undefined)]).toEqual([
      "Linux", "Windows", "macOS", "freebsd", "Unknown platform",
    ]);
  });
});
