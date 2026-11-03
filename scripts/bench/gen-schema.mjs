#!/usr/bin/env node
// Benchmark fixture (§14): 600 tables, 9,000 columns, 400 procedures, 150 views, 300 FKs, 200 triggers, a 12,000-line
// procedure with 40 CTEs and 6 MERGE statements, a 10,000-line PL/pgSQL function (metric 27), and a 1,000,000-row source
// for the 40-column result query.
//
//   node scripts/bench/gen-schema.mjs mssql > fixture.sql   |   node scripts/bench/gen-schema.mjs pg > fixture.sql
//   node scripts/bench/gen-schema.mjs --apply               (loads both into the docker fixture)
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { waitForFixture } from "../fixture/wait-ready.mjs";
import { fixturePassword } from "../fixture/fixture-secrets.mjs";

const TABLES = 600;
const COLS_PER_TABLE = 15; // 600 × 15 = 9,000
const PROCS = 400;
const VIEWS = 150;
const FKS = 300;
const TRIGGERS = 200;

export function bigProcBody(dialect) {
  // 12,000 lines with 40 CTEs and 6 MERGE statements.
  const L = [];
  const mergeAt = new Set([5, 12, 19, 26, 33, 39]);
  for (let c = 1; c <= 40; c++) {
    const t = `t${(c % TABLES) + 1}`;
    L.push(dialect === "pg" ? `    CREATE TEMP TABLE IF NOT EXISTS tmp_${c} ON COMMIT DROP AS WITH c${c} AS (` : `    ;WITH c${c} AS (`);
    L.push(`        SELECT a.id, a.c1, a.c2, a.c3, a.c4, a.amount, a.created_at`);
    L.push(`        FROM ${dialect === "pg" ? "public" : "dbo"}.${t} a`);
    L.push(`        WHERE a.c1 > ${c} AND a.created_at >= ${dialect === "pg" ? "p_start" : "@StartDate"}`);
    L.push(`    )`);
    L.push(dialect === "pg" ? `    SELECT x.id, x.c1, x.amount FROM c${c} x WHERE x.amount > 0;` : `    SELECT x.id, x.c1, x.amount INTO #tmp_${c} FROM c${c} x WHERE x.amount > 0;`);
    if (mergeAt.has(c)) {
      const target = `${dialect === "pg" ? "public" : "dbo"}.t${c + 1}`;
      if (dialect === "pg") {
        L.push(`    MERGE INTO ${target} AS t USING tmp_${c} AS s ON t.id = s.id`);
        L.push(`    WHEN MATCHED THEN UPDATE SET c1 = s.c1`);
        L.push(`    WHEN NOT MATCHED THEN INSERT (id, c1) VALUES (s.id, s.c1);`);
      } else {
        L.push(`    MERGE ${target} AS t USING #tmp_${c} AS s ON t.id = s.id`);
        L.push(`    WHEN MATCHED THEN UPDATE SET t.c1 = s.c1`);
        L.push(`    WHEN NOT MATCHED THEN INSERT (id, c1) VALUES (s.id, s.c1);`);
      }
    }
    // Filler statements that look like real procedural code.
    const filler = Math.floor((12000 - 40 * 8) / 40) - 2;
    for (let k = 0; k < filler; k += 6) {
      if (dialect === "pg") {
        L.push(`    v_counter := v_counter + ${k};`);
        L.push(`    IF v_counter > ${k * 3} THEN`);
        L.push(`        UPDATE public.${t} SET c2 = c2 + 1 WHERE id = v_counter AND c3 = 'x${k}';`);
        L.push(`    ELSE`);
        L.push(`        v_total := v_total + (SELECT COALESCE(SUM(amount), 0) FROM public.${t} WHERE c1 = ${k});`);
        L.push(`    END IF;`);
      } else {
        L.push(`    SET @Counter = @Counter + ${k};`);
        L.push(`    IF @Counter > ${k * 3}`);
        L.push(`        UPDATE dbo.${t} SET c2 = c2 + 1 WHERE id = @Counter AND c3 = N'x${k}';`);
        L.push(`    ELSE`);
        L.push(`        SELECT @Total = @Total + ISNULL(SUM(amount), 0) FROM dbo.${t} WHERE c1 = ${k};`);
        L.push(`    -- step ${c}.${k}`);
      }
    }
  }
  return L;
}

export function bigProc(dialect) {
  const body = bigProcBody(dialect);
  if (dialect === "pg") {
    return [
      "CREATE OR REPLACE FUNCTION public.usp_bench_big(p_customer int, p_start timestamptz) RETURNS int LANGUAGE plpgsql AS $$",
      "DECLARE v_counter int := 0; v_total numeric := 0;",
      "BEGIN",
      ...body,
      "    RETURN v_counter;",
      "END",
      "$$;",
    ].join("\n");
  }
  return [
    "CREATE OR ALTER PROCEDURE dbo.usp_BenchBig @CustomerId int, @StartDate datetime2 = NULL AS",
    "BEGIN",
    "    SET NOCOUNT ON;",
    "    DECLARE @Counter int = 0, @Total decimal(18,2) = 0;",
    ...body,
    "END;",
  ].join("\n");
}

/**
 * Metric 27: a 10,000-line PL/pgSQL function with what tier 2 resolves — RETURNS TABLE columns, DECLARE variables,
 * FOR rec IN record loops, a temp table created in the body, CTEs — under a named $body$ quote.
 */
export function bigPlpgsql() {
  const L = [
    "CREATE OR REPLACE FUNCTION public.fn_bench_plpgsql(p_customer int, p_since timestamptz DEFAULT NULL)",
    "RETURNS TABLE (out_id int, out_total numeric, out_label text) LANGUAGE plpgsql AS $body$",
    "DECLARE",
    "  v_counter int := 0;",
    "  v_total numeric(18,2) := 0;",
    "  v_label text;",
    "  rec record;",
    "BEGIN",
    "  CREATE TEMP TABLE IF NOT EXISTS tmp_bench_rows (id int, amount numeric(18,2), label text) ON COMMIT DROP;",
  ];
  for (let k = 0; L.length < 9994; k++) {
    const t = `t${(k % TABLES) + 1}`;
    L.push(
      `  FOR rec IN SELECT a.id, a.amount, a.c3 FROM public.${t} a WHERE a.c1 > ${k} AND a.created_at >= COALESCE(p_since, now() - interval '1 day') LOOP`,
      `    v_counter := v_counter + 1;`,
      `    v_total := v_total + COALESCE(rec.amount, 0);`,
      `    INSERT INTO tmp_bench_rows (id, amount, label) VALUES (rec.id, rec.amount, rec.c3);`,
      `  END LOOP;`,
      `  WITH s${k} AS (SELECT r.id, r.amount FROM tmp_bench_rows r WHERE r.amount > ${k % 100})`,
      `  SELECT count(*), max(x.label) INTO v_counter, v_label FROM s${k} JOIN tmp_bench_rows x ON x.id = s${k}.id;`,
      `  -- step ${k}`,
    );
  }
  L.push("  RETURN QUERY SELECT t.id, t.amount, t.label FROM tmp_bench_rows t;", "END", "$body$;");
  return L.join("\n");
}

function mssql() {
  const out = [];
  out.push("IF DB_ID('SalesDB') IS NULL CREATE DATABASE SalesDB;", "GO", "USE SalesDB;", "GO");
  out.push(`IF OBJECT_ID('dbo.Customers') IS NULL CREATE TABLE dbo.Customers (CustomerId int NOT NULL PRIMARY KEY, FullName nvarchar(200) NOT NULL, Email nvarchar(320) NULL);`);
  out.push(`IF OBJECT_ID('dbo.Orders') IS NULL CREATE TABLE dbo.Orders (OrderId bigint IDENTITY PRIMARY KEY, CustomerId int NOT NULL REFERENCES dbo.Customers(CustomerId), OrderDate datetime2 NOT NULL, TotalAmount decimal(18,2) NOT NULL, Status nvarchar(20) NOT NULL);`);
  out.push(`IF OBJECT_ID('dbo.OrderTotals') IS NULL CREATE TABLE dbo.OrderTotals (CustomerId int PRIMARY KEY, Total decimal(18,2));`, "GO");
  for (let i = 1; i <= TABLES; i++) {
    const cols = ["id int NOT NULL PRIMARY KEY", "c1 int NULL", "c2 int NULL", "c3 nvarchar(50) NULL", "c4 nvarchar(100) NULL", "amount decimal(18,2) NULL", "created_at datetime2 NULL"];
    for (let k = cols.length; k < COLS_PER_TABLE; k++) cols.push(`extra${k} ${k % 3 === 0 ? "bigint" : k % 3 === 1 ? "nvarchar(40)" : "datetime2"} NULL`);
    out.push(`IF OBJECT_ID('dbo.t${i}') IS NULL CREATE TABLE dbo.t${i} (${cols.join(", ")});`);
  }
  out.push("GO");
  for (let i = 1; i <= FKS; i++) {
    const parent = i + 1;
    const ref = Math.max(1, Math.floor(i / 2));
    out.push(`IF OBJECT_ID('dbo.fk_t${parent}_t${ref}') IS NULL ALTER TABLE dbo.t${parent} ADD CONSTRAINT fk_t${parent}_t${ref} FOREIGN KEY (c1) REFERENCES dbo.t${ref}(id);`);
  }
  out.push("GO");
  for (let i = 1; i <= VIEWS; i++) out.push(`CREATE OR ALTER VIEW dbo.v${i} AS SELECT a.id, a.c1, a.c3, b.c4 FROM dbo.t${i} a LEFT JOIN dbo.t${i + 1} b ON b.id = a.c1;`, "GO");
  for (let i = 1; i <= PROCS; i++)
    out.push(`CREATE OR ALTER PROCEDURE dbo.usp_p${i} @Id int, @From datetime2 = NULL, @Count int OUTPUT AS BEGIN SET NOCOUNT ON; SELECT @Count = COUNT(*) FROM dbo.t${(i % TABLES) + 1} WHERE id = @Id; END;`, "GO");
  for (let i = 1; i <= TRIGGERS; i++)
    out.push(`CREATE OR ALTER TRIGGER dbo.tr_t${i} ON dbo.t${i} AFTER UPDATE AS BEGIN SET NOCOUNT ON; IF UPDATE(amount) BEGIN DECLARE @n int = (SELECT COUNT(*) FROM inserted i JOIN deleted d ON d.id = i.id WHERE i.amount <> d.amount); END END;`, "GO");
  out.push(bigProc("mssql"), "GO");
  // 1,000,000-row source for the 40-column result query (metric 11).
  out.push(`IF OBJECT_ID('dbo.Big') IS NULL BEGIN
    CREATE TABLE dbo.Big (id int NOT NULL PRIMARY KEY, a int, b nvarchar(40), c decimal(18,2), d datetime2);
    INSERT dbo.Big (id, a, b, c, d)
    SELECT n, n % 977, CONCAT(N'name-', n % 1000), (n % 100000) / 100.0, DATEADD(SECOND, n, '2026-01-01')
    FROM (SELECT TOP (1000000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c) x;
END;`, "GO");
  out.push(`IF NOT EXISTS (SELECT 1 FROM dbo.Customers) INSERT dbo.Customers VALUES (42, N'Acme Global Corp', N'ap@acme.example'), (7, N'Contoso', NULL);`, "GO");
  return out.join("\n");
}

function pg() {
  const out = [];
  out.push("CREATE EXTENSION IF NOT EXISTS pldbgapi;");
  // Activity monitor (Phase B): both are preloaded by postgres.Dockerfile, but their views and functions exist only once
  // created in the database the monitor connects to (top queries, server-profile waits; D2, D7, D14, T1, T11).
  out.push("CREATE EXTENSION IF NOT EXISTS pg_stat_statements;");
  out.push("CREATE EXTENSION IF NOT EXISTS pg_wait_sampling;");
  out.push(`CREATE TABLE IF NOT EXISTS public.customers (customer_id int PRIMARY KEY, full_name text NOT NULL, email text);`);
  out.push(`CREATE TABLE IF NOT EXISTS public.orders (order_id bigserial PRIMARY KEY, customer_id int NOT NULL REFERENCES public.customers, order_date timestamptz NOT NULL, total_amount numeric(18,2) NOT NULL, status text NOT NULL);`);
  for (let i = 1; i <= TABLES; i++) {
    const cols = ["id int PRIMARY KEY", "c1 int", "c2 int", "c3 varchar(50)", "c4 varchar(100)", "amount numeric(18,2)", "created_at timestamptz"];
    for (let k = cols.length; k < COLS_PER_TABLE; k++) cols.push(`extra${k} ${k % 3 === 0 ? "bigint" : k % 3 === 1 ? "varchar(40)" : "timestamptz"}`);
    out.push(`CREATE TABLE IF NOT EXISTS public.t${i} (${cols.join(", ")});`);
  }
  for (let i = 1; i <= FKS; i++) {
    const parent = i + 1;
    const ref = Math.max(1, Math.floor(i / 2));
    out.push(`DO $$ BEGIN ALTER TABLE public.t${parent} ADD CONSTRAINT fk_t${parent}_t${ref} FOREIGN KEY (c1) REFERENCES public.t${ref}(id); EXCEPTION WHEN duplicate_object THEN NULL; END $$;`);
  }
  for (let i = 1; i <= VIEWS; i++) out.push(`CREATE OR REPLACE VIEW public.v${i} AS SELECT a.id, a.c1, a.c3, b.c4 FROM public.t${i} a LEFT JOIN public.t${i + 1} b ON b.id = a.c1;`);
  for (let i = 1; i <= PROCS; i++)
    out.push(`CREATE OR REPLACE FUNCTION public.fn_p${i}(p_id int, p_from timestamptz DEFAULT NULL) RETURNS bigint LANGUAGE plpgsql AS $$ DECLARE n bigint; BEGIN SELECT count(*) INTO n FROM public.t${(i % TABLES) + 1} WHERE id = p_id; RETURN n; END $$;`);
  out.push(`CREATE OR REPLACE FUNCTION public.tr_touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.created_at := now(); RETURN NEW; END $$;`);
  for (let i = 1; i <= TRIGGERS; i++)
    out.push(`CREATE OR REPLACE TRIGGER tr_t${i} BEFORE UPDATE OF amount ON public.t${i} FOR EACH ROW WHEN (OLD.amount IS DISTINCT FROM NEW.amount) EXECUTE FUNCTION public.tr_touch();`);
  out.push(bigProc("pg"));
  out.push(bigPlpgsql());
  out.push(`CREATE TABLE IF NOT EXISTS public.big AS SELECT n AS id, n % 977 AS a, 'name-' || (n % 1000) AS b, (n % 100000) / 100.0 AS c, timestamptz '2026-01-01' + n * interval '1 second' AS d FROM generate_series(1, 1000000) n;`);
  out.push(`INSERT INTO public.customers VALUES (42, 'Acme Global Corp', 'ap@acme.example') ON CONFLICT DO NOTHING;`);
  out.push(`CREATE OR REPLACE FUNCTION public.calc_discount(p_total numeric, p_pct int DEFAULT 10) RETURNS numeric LANGUAGE plpgsql AS $$
DECLARE
  v_rate numeric := p_pct / 100.0;
  v_result numeric;
BEGIN
  IF p_total > 1000 THEN
    v_rate := v_rate + 0.05;
  END IF;
  v_result := round(p_total * (1 - v_rate), 2);
  RAISE NOTICE 'discount % -> %', p_total, v_result;
  RETURN v_result;
END
$$;`);
  return out.join("\n");
}

/** 40-column result query over 1M rows (metric 11). */
export function bigResultQuery(dialect) {
  const cols = [];
  for (let i = 0; i < 40; i++) {
    const src = ["id", "a", "b", "c", "d"][i % 5];
    cols.push(`${src} AS col${i}`);
  }
  return dialect === "pg" ? `SELECT ${cols.join(", ")} FROM public.big` : `SELECT ${cols.join(", ")} FROM dbo.Big`;
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
const arg = isMain ? process.argv[2] : undefined;
if (arg === "mssql") process.stdout.write(mssql());
else if (arg === "pg") process.stdout.write(pg());
else if (arg === "--apply") {
  const dir = path.join(os.tmpdir(), "querithm-fixture");
  mkdirSync(dir, { recursive: true });
  writeFileSync(path.join(dir, "mssql.sql"), mssql());
  writeFileSync(path.join(dir, "pg.sql"), pg());
  const project = "querithm-fixture";
  const run = (svc, cmd, file) => {
    const r = spawnSync("docker", ["compose", "-p", project, "-f", path.join(path.dirname(fileURLToPath(import.meta.url)), "docker", "compose.yml"), "exec", "-T", svc, ...cmd], {
      input: readFileSync(file), stdio: ["pipe", "inherit", "inherit"], maxBuffer: 1 << 30,
    });
    if (r.status !== 0) process.exit(r.status ?? 1);
  };
  waitForFixture(["mssql", "postgres"]);
  console.log("loading SQL Server fixture…");
  run("mssql", ["/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-P", fixturePassword("mssql"), "-C", "-b", "-I"], path.join(dir, "mssql.sql"));
  console.log("loading PostgreSQL fixture…");
  run("postgres", ["psql", "-U", "postgres", "-v", "ON_ERROR_STOP=1", "-q"], path.join(dir, "pg.sql"));
  console.log("fixture ready");
} else if (arg) {
  console.error("usage: gen-schema.mjs mssql|pg|--apply");
  process.exit(1);
}
