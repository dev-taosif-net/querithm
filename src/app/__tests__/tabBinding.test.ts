import { describe, expect, it } from "vitest";
import { newTabBinding } from "../tabBinding";

const none = () => false;
const defaults = (id: string) => ({ p1: "master", p2: "postgres" })[id];
const active = { profileId: "p1", database: "SalesArchive" };

describe("new query tab binding", () => {
  it("inherits the active tab's server and database (Ctrl+N: new query on the same connection)", () => {
    expect(newTabBinding({}, active, none, defaults)).toEqual(active);
  });
  it("does not inherit a server the user disconnected: the new tab has no connection", () => {
    expect(newTabBinding({}, active, (id) => id === "p1", defaults)).toEqual({});
  });
  it("an explicitly requested server is used even if another tab's server is disconnected", () => {
    expect(newTabBinding({ profileId: "p2" }, active, (id) => id === "p1", defaults)).toEqual({ profileId: "p2", database: "postgres" });
  });
  it("same server as the active tab: keeps its database unless one is given", () => {
    expect(newTabBinding({ profileId: "p1" }, active, none, defaults)).toEqual(active);
    expect(newTabBinding({ profileId: "p1", database: "Inventory" }, active, none, defaults)).toEqual({ profileId: "p1", database: "Inventory" });
  });
  it("no active tab, or an empty profile, gives an unbound tab; a server without a default database has none", () => {
    expect(newTabBinding({}, undefined, none, defaults)).toEqual({});
    expect(newTabBinding({ profileId: "" }, active, none, defaults)).toEqual({});
    expect(newTabBinding({ profileId: "p9" }, active, none, defaults)).toEqual({ profileId: "p9", database: undefined });
  });
});
