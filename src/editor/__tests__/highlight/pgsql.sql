-- T1 corpus, PostgreSQL: every Monarch rule of pgLanguage() fires at least once (highlightGolden.test.ts checks it).
SELECT E'tab\tand\\slash''q', e'x', 'plain''s', "Quoted""Name", 1.5e-3, 10., $1::int4, :named, arr[1], a @> b, x ~* 'y', j #>> '{a}';
SELECT a::"Mixed"[], b ::  int8, c::timestamptz, d IS DISTINCT FROM e, f ISNULL, g NOTNULL, h SIMILAR TO 'p', i ILIKE 'q';
/* outer /* nested */ * / still */ SELECT 1;
CREATE FUNCTION sales.f(p int) RETURNS int LANGUAGE plpgsql AS $body$
DECLARE r RECORD; n int := 0;
BEGIN
  -- comment in body
  /* block in body /* nested */ * / */
  FOR r IN SELECT * FROM t WHERE t.a IS NOT NULL AND t.b ILIKE 'x' AND t.c = TRUE LOOP
    n := n + 1.5; RAISE NOTICE 'row % "q"', r."Col";
  END LOOP;
  PERFORM $inner$ nested tag $inner$;
  RETURN coalesce(n, 0) + count(*) - 1;
END;
$body$;
DO $$ BEGIN NULL; END $$;
SELECT $tag$ one line $tag$, $$
multi-line
$$ AS body;
