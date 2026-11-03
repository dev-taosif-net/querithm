// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import type { MonitorSnapshot } from "../../ipc/contract";
import { MonitorModel } from "../model";
import { useTopics } from "../useModel";

afterEach(cleanup);

const snap = (seq: number, o: Partial<MonitorSnapshot> = {}): MonitorSnapshot => ({
  monitorId: "m", at: Date.now(), seq, sessions: [], status: [{ pane: "sessions", state: "ok" }], lagMs: 0, connectionCount: 1, throttled: false, paused: false, connection: "connected", ...o,
});

describe("useTopics", () => {
  it("re-renders on each emit of its topics", () => {
    const model = new MonitorModel("m", async () => ({ texts: {}, missing: [] }) as never);
    const View = () => {
      useTopics(model, ["connection"]);
      return <span data-testid="conn">{model.connection}</span>;
    };
    render(<View />);
    expect(screen.getByTestId("conn").textContent).toBe("connecting");
    act(() => void model.apply(snap(1)));
    expect(screen.getByTestId("conn").textContent).toBe("connected");
  });

  it("does not lose a snapshot that arrives after the render and before the subscription", () => {
    // The window between a view's first render and its (passive-effect) subscription: a monitor tick there used to leave
    // the pane stale until the next tick, and made the ActivityTab tests flaky under load.
    const model = new MonitorModel("m", async () => ({ texts: {}, missing: [] }) as never);
    let delivered = false;
    const View = () => {
      useTopics(model, ["connection", "status"]);
      const shown = model.connection;
      if (!delivered) {
        delivered = true;
        model.apply(snap(1, { connection: "reconnecting" })); // after this render read the model, before it subscribed
      }
      return <span data-testid="conn">{shown}</span>;
    };
    render(<View />);
    expect(screen.getByTestId("conn").textContent).toBe("reconnecting");
  });
});
