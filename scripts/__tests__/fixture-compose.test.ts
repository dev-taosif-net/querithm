import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

// The Docker fixture's credentials are committed on purpose (CLAUDE.md §3), so they must never work outside this
// machine: every published port binds to loopback.
const compose = readFileSync(new URL("../bench/docker/compose.yml", import.meta.url), "utf8").split("\n");

function publishedPorts(): string[] {
  const out: string[] = [];
  compose.forEach((line, i) => {
    const m = /^(\s*)ports:\s*(.*)$/.exec(line);
    if (!m) return;
    if (m[2].trim()) {
      out.push(...(JSON.parse(m[2]) as string[]));
      return;
    }
    for (let j = i + 1; j < compose.length; j++) {
      const item = /^\s*-\s*["']?([^"'\s]+)["']?\s*$/.exec(compose[j]);
      if (!item) break;
      out.push(item[1]);
    }
  });
  return out;
}

describe("Docker fixture compose file", () => {
  it("publishes every port on 127.0.0.1 only", () => {
    const ports = publishedPorts();
    expect(ports.length).toBeGreaterThanOrEqual(9);
    for (const p of ports) expect(p.startsWith("127.0.0.1:"), p).toBe(true);
  });
});
