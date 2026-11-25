-- T1 corpus, every dialect: the token kinds of plans/app-themes §5.2 (constants, word operators, casts, binds,
-- system variables) and the semantic forms of §5.3 (CTE names, schema prefixes), in upper and lower case.
SELECT TRUE, FALSE, NULL, true AS t, false AS f, null AS n, NOT NULL;
SELECT a FROM t WHERE a AND b OR NOT c IN (1, 2) AND d LIKE 'x%' AND e BETWEEN 1 AND 2 AND f IS NULL AND g IS NOT TRUE;
select a from t where a and b or not c in (1) and d like 'y' and e between 1 and 2 and f is not null;
SELECT a ILIKE 'x', b SIMILAR TO 'y', c ISNULL, d NOTNULL, e GLOB 'z*', f REGEXP '^a', g MATCH 'q', j RLIKE 'k';
SELECT h XOR i, 7 DIV 2, 7 MOD 2, 7 % 2 FROM dual;
SELECT a::int, b::"T"[], c :: text, d::INT[], NULL::JSONB, '1'::numeric(12, 2) FROM t;
SELECT $1, $2, $name, :name, :1, :"x", ?, ?1, ?12, @p, @x, @@x, @@ROWCOUNT, @@global.max_connections, $x;
WITH recent AS (SELECT * FROM sales.Orders o WHERE o.placed_at > CURRENT_DATE)
SELECT r.*, c.name FROM recent r JOIN dbo.Customers c ON c.id = r.customer_id WHERE r.total > ? AND c.region = :region;
INSERT INTO sales.Orders (id, note) VALUES (@id, 'it''s'), ($1, NULL);
UPDATE sales.Orders SET note = NULL WHERE id IN (SELECT id FROM recent) AND flag IS TRUE;
