import { describe, expect, it } from "vitest";
// @ts-expect-error plain .mjs dev script without type declarations
import { isAllowedOrigin, parseMessage } from "../dev-bridge-guard.mjs";

describe("dev bridge origin check", () => {
  it("accepts the dev UI on 127.0.0.1 and localhost", () => {
    expect(isAllowedOrigin("http://127.0.0.1:1420", 1420)).toBe(true);
    expect(isAllowedOrigin("http://localhost:1420", 1420)).toBe(true);
  });

  it("rejects other sites, ports, schemes and a missing origin", () => {
    for (const o of ["https://evil.example", "http://127.0.0.1:8080", "https://127.0.0.1:1420", "http://127.0.0.1:1420.evil.example", "null", "", undefined])
      expect(isAllowedOrigin(o, 1420)).toBe(false);
  });

  it("follows a custom UI port", () => {
    expect(isAllowedOrigin("http://127.0.0.1:5000", 5000)).toBe(true);
    expect(isAllowedOrigin("http://127.0.0.1:1420", 5000)).toBe(false);
  });
});

describe("dev bridge message parsing", () => {
  it("parses a well-formed command", () => {
    expect(parseMessage('{"id":3,"cmd":"rpc","args":{"method":"profile.list"}}')).toEqual({ id: 3, cmd: "rpc", args: { method: "profile.list" } });
    expect(parseMessage(Buffer.from('{"id":0,"cmd":"app_info"}'))).toEqual({ id: 0, cmd: "app_info", args: {} });
  });

  it("returns null instead of throwing on malformed frames", () => {
    for (const raw of ["not json", "null", "[]", '"x"', '{"cmd":"rpc"}', '{"id":"1","cmd":"rpc"}', '{"id":1.5,"cmd":"rpc"}', '{"id":-1,"cmd":"rpc"}', '{"id":4294967296,"cmd":"rpc"}', '{"id":1}'])
      expect(parseMessage(raw)).toBeNull();
  });
});
