import { describe, it, expect } from "vitest";
import {
  bestMatch, humpMatch, humpStarts, matchRank, sortText,
  MATCH_HUMP, MATCH_NONE, MATCH_PREFIX, MATCH_SUBSTRING, type RankKey,
} from "../rank";

const key = (o: Partial<RankKey> = {}): RankKey => ({ tier: 0, match: 0, group: 0, usage: 0, order: 0, label: "x", ...o });

describe("rank: hump starts", () => {
  it("empty string has no humps", () => expect(humpStarts("")).toEqual([]));
  it("camelCase and PascalCase", () => {
    expect(humpStarts("CustomerId")).toEqual([0, 8]);
    expect(humpStarts("orderDate")).toEqual([0, 5]);
  });
  it("acronym: the last capital before lowercase starts a hump", () => expect(humpStarts("XMLParser")).toEqual([0, 3]));
  it("all caps is a single hump", () => expect(humpStarts("ORDERS")).toEqual([0]));
  it("separators start a hump on the next letter", () => {
    expect(humpStarts("usp_GetCustomerOrders")).toEqual([0, 4, 7, 15]);
    expect(humpStarts("order_date")).toEqual([0, 6]);
    expect(humpStarts("a b")).toEqual([0, 2]);
    expect(humpStarts("x.y-z$w")).toEqual([0, 2, 4, 6]);
  });
  it("leading separator: index 0 and the first letter both count", () => expect(humpStarts("_id")).toEqual([0, 1]));
  it("digit runs start a hump; a lowercase letter after digits does not", () => {
    expect(humpStarts("Order2Total")).toEqual([0, 5, 6]);
    expect(humpStarts("abc123def")).toEqual([0, 3]);
  });
  it("non-ASCII characters are treated as alphanumeric (no hump after them)", () => expect(humpStarts("Äbc")).toEqual([0]));
});

describe("rank: hump match", () => {
  it("empty prefix matches anything", () => expect(humpMatch("", "Orders")).toBe(true));
  it("prefix longer than the candidate never matches", () => expect(humpMatch("Ordersx", "Orders")).toBe(false));
  it("first character must match (case-insensitive)", () => {
    expect(humpMatch("cId", "CustomerId")).toBe(true);
    expect(humpMatch("CI", "CustomerId")).toBe(true);
    expect(humpMatch("id", "CustomerId")).toBe(false);
  });
  it("letters must sit at hump starts or continue the current hump", () => {
    expect(humpMatch("cusi", "CustomerId")).toBe(true);
    expect(humpMatch("cx", "CustomerId")).toBe(false);
    expect(humpMatch("ce", "CustomerId")).toBe(false); // 'e' is mid-hump, not a hump start
  });
  it("underscore names: typed separators and skipped separators", () => {
    expect(humpMatch("usp_gco", "usp_GetCustomerOrders")).toBe(true);
    expect(humpMatch("ugco", "usp_GetCustomerOrders")).toBe(true);
    expect(humpMatch("o_d", "order_date")).toBe(true);
    expect(humpMatch("od", "order_date")).toBe(true);
  });
  it("needs backtracking: an early greedy continuation fails, a later hump succeeds", () => {
    // After G and the first O, the next 'o' cannot continue "Order" ('r'), so it must jump to the second "Orders" hump.
    expect(humpMatch("goo", "GetOrderOrders")).toBe(true);
    expect(humpMatch("gooo", "GetOrderOrders")).toBe(false);
  });
  it("humps out of order do not match", () => expect(humpMatch("coc", "CustomerOrders")).toBe(false));
});

describe("rank: match rank", () => {
  it("empty prefix is a prefix match, even for an empty candidate", () => {
    expect(matchRank("", "anything")).toBe(MATCH_PREFIX);
    expect(matchRank("", "")).toBe(MATCH_PREFIX);
  });
  it("empty candidate with a prefix never matches", () => expect(matchRank("a", "")).toBe(MATCH_NONE));
  it("prefix (case-insensitive)", () => {
    expect(matchRank("cust", "CustomerId")).toBe(MATCH_PREFIX);
    expect(matchRank("CUST", "customerid")).toBe(MATCH_PREFIX);
  });
  it("hump beats substring", () => expect(matchRank("cId", "CustomerId")).toBe(MATCH_HUMP));
  it("substring when neither prefix nor hump", () => {
    expect(matchRank("tomer", "CustomerId")).toBe(MATCH_SUBSTRING);
    expect(matchRank("ci", "AcidLevel")).toBe(MATCH_SUBSTRING);
  });
  it("no match", () => expect(matchRank("zz", "CustomerId")).toBe(MATCH_NONE));
  it("whole-word equals is a prefix match", () => expect(matchRank("Orders", "orders")).toBe(MATCH_PREFIX));
});

describe("rank: best match", () => {
  it("no candidates", () => {
    expect(bestMatch("a", [])).toBe(MATCH_NONE);
    // Documents current behaviour: with no candidates even an empty prefix reports no match.
    expect(bestMatch("", [])).toBe(MATCH_NONE);
  });
  it("picks the lowest rank across candidates and ignores misses", () => {
    expect(bestMatch("cid", ["zzz", "AcidLevel", "CustomerId"])).toBe(MATCH_HUMP);
    expect(bestMatch("ord", ["dbo.Orders", "Orders"])).toBe(MATCH_PREFIX);
    expect(bestMatch("ord", ["dbo.Orders", "zz"])).toBe(MATCH_SUBSTRING);
  });
  it("all misses", () => expect(bestMatch("q", ["a", "b"])).toBe(MATCH_NONE));
});

describe("rank: sortText", () => {
  it("encodes fields as fixed-width zero-padded segments", () => {
    expect(sortText(key({ tier: 0, match: 1, group: 3, usage: 5, order: 2, label: "Foo" }))).toBe("0103999994" + "0002foo");
  });
  it("label is lowercased so ties break case-insensitively", () => {
    expect(sortText(key({ label: "ABC" }))).toBe(sortText(key({ label: "abc" })));
  });
  it("string order follows tier, then match, then group", () => {
    const a = sortText(key({ tier: 0, match: 2, group: 9 }));
    const b = sortText(key({ tier: 1, match: 0, group: 0 }));
    expect(a < b).toBe(true);
    expect(sortText(key({ match: 0, group: 5 })) < sortText(key({ match: 1, group: 0 }))).toBe(true);
    expect(sortText(key({ group: 2 })) < sortText(key({ group: 10 }))).toBe(true);
  });
  it("higher usage sorts first; usage above the cap saturates", () => {
    expect(sortText(key({ usage: 10 })) < sortText(key({ usage: 9 }))).toBe(true);
    expect(sortText(key({ usage: 999999 }))).toBe(sortText(key({ usage: 5_000_000 })));
  });
  it("order within a group, then label", () => {
    expect(sortText(key({ order: 2, label: "b" })) < sortText(key({ order: 10, label: "a" }))).toBe(true);
    expect(sortText(key({ order: 1, label: "a" })) < sortText(key({ order: 1, label: "b" }))).toBe(true);
  });
  it("fractional and negative order are floored / clamped to 0", () => {
    expect(sortText(key({ order: 3.9 }))).toBe(sortText(key({ order: 3 })));
    expect(sortText(key({ order: -4 }))).toBe(sortText(key({ order: 0 })));
  });
  it("values wider than their segment keep only the low digits (documents current behaviour)", () => {
    // pad() truncates to the last `w` digits: order 10000 encodes as "0000" and sorts before order 1.
    expect(sortText(key({ order: 10000 }))).toBe(sortText(key({ order: 0 })));
    expect(sortText(key({ group: 103 }))).toBe(sortText(key({ group: 3 })));
  });
  it("negative usage ranks like zero usage (clamp usage at 0 before encoding)", () => {
    expect(sortText(key({ usage: -5 }))).toBe(sortText(key({ usage: 0 })));
  });
});
