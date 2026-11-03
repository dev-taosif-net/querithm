import { describe, it, expect } from "vitest";
import { isAliasKeyword, suggestAlias } from "../alias";

describe("suggestAlias", () => {
  it("snake_case initials, capitals, else first 3 letters", () => {
    expect(suggestAlias("sales_order_line")).toBe("sol");
    expect(suggestAlias("SALES_ORDER")).toBe("so");
    expect(suggestAlias("SalesOrderHeader")).toBe("soh");
    expect(suggestAlias("tblCustomerAccountSummary")).toBe("cas");
    expect(suggestAlias("Customer")).toBe("cus");
    expect(suggestAlias("ORDERS")).toBe("ord");
    expect(suggestAlias("customerId")).toBe("cus");
    expect(suggestAlias("[Order Details]")).toBe("od");
  });
  it("never a keyword, type or built-in function", () => {
    expect(suggestAlias("Users")).toBe("usr");          // use
    expect(suggestAlias("OrderNumber")).toBe("ord");    // on
    expect(suggestAlias("Address")).toBe("addr");       // add, skeleton add → first 4
    expect(suggestAlias("Endpoints")).toBe("endp");     // end, skeleton end → first 4
    expect(suggestAlias("Settings")).toBe("stt");       // set
    expect(suggestAlias("Topics")).toBe("tpc");         // top
    expect(suggestAlias("Maximums")).toBe("mxm");       // max
    expect(suggestAlias("Ascii")).toBe("asci");         // asc
    expect(suggestAlias("Rows")).toBe("rws");           // row
    for (const n of ["Users", "OrderNumber", "Address", "Endpoints", "Settings", "Topics", "Ascii", "Intervals", "Keys", "Rows", "Bytes", "Dates", "Lines"]) {
      expect(isAliasKeyword(suggestAlias(n))).toBe(false);
    }
  });
  it("never starts with a digit", () => {
    expect(suggestAlias("2019_sales")).toMatch(/^\p{L}/u);
  });
  it("numbers collisions, case-insensitively", () => {
    expect(suggestAlias("SalesOrderHeader", ["SOH"])).toBe("soh2");
    expect(suggestAlias("SalesOrderHeader", ["soh", "soh2"])).toBe("soh3");
  });
});
