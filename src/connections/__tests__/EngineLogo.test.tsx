// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { EngineLogo } from "../EngineLogo";

afterEach(cleanup);

describe("EngineLogo", () => {
  it("draws the PostgreSQL mark for pg at the requested size", () => {
    render(<EngineLogo engine="pg" size={20} />);
    const svg = screen.getByLabelText("PostgreSQL");
    expect(svg.getAttribute("width")).toBe("20");
  });
  it("draws the SQL Server mark for mssql", () => {
    render(<EngineLogo engine="mssql" />);
    expect(screen.getByLabelText("Microsoft SQL Server").getAttribute("width")).toBe("14");
  });
  it.each([["sqlite", "SQLite"], ["mySql", "MySQL / MariaDB"]] as const)("draws Querithm's own mark for %s, named for screen readers", (engine, name) => {
    const { container } = render(<EngineLogo engine={engine} size={16} />);
    expect(screen.getByLabelText(name).getAttribute("width")).toBe("16");
    expect(container.querySelector("title")?.textContent).toBe(name);
  });
  it("falls back to the SQL Server mark when the engine is unknown", () => {
    render(<EngineLogo engine={undefined} />);
    expect(screen.getByLabelText("Microsoft SQL Server")).toBeTruthy();
    expect(screen.queryByLabelText("PostgreSQL")).toBeNull();
  });
});

describe("EngineLogo decorative (tab strip)", () => {
  it("is hidden from assistive technology and has no title, for both engines", () => {
    for (const engine of ["mssql", "pg"] as const) {
      const { container, unmount } = render(<EngineLogo engine={engine} mono decorative size={10} />);
      const svg = container.querySelector("svg")!;
      expect(svg.getAttribute("aria-hidden")).toBe("true");
      expect(svg.getAttribute("role")).toBeNull();
      expect(svg.querySelector("title")).toBeNull();
      unmount();
    }
  });
});
