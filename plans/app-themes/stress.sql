/* ============================================================================
   Monthly revenue health report — stress fixture for syntax palettes
   Nested CTEs, JSONB, window functions, casts, params, inline comments.
   ============================================================================ */
WITH RECURSIVE region_tree AS (
    -- anchor: top-level regions
    SELECT r.region_id, r.parent_id, r.name, 1 AS depth
    FROM sales.regions AS r
    WHERE r.parent_id IS NULL
    UNION ALL
    SELECT c.region_id, c.parent_id, c.name, rt.depth + 1
    FROM sales.regions c
    JOIN region_tree rt ON rt.region_id = c.parent_id
    WHERE rt.depth < 8
),
active_customers AS (
    SELECT cu.customer_id,
           cu.profile ->> 'tier'                    AS tier,
           (cu.profile -> 'flags' @> '["vip"]')     AS is_vip,
           cu.profile #>> '{address,country}'       AS country,
           COALESCE(cu.credit_limit, 0)::NUMERIC(12, 2) AS credit_limit
    FROM public.customers cu
    WHERE cu.deleted_at IS NULL
      AND cu.created_at >= $1::TIMESTAMP            -- report start
      AND cu.status IN ('active', 'trial', 'grace')
      AND NOT cu.is_test_account = TRUE
),
orders_enriched AS (
    SELECT o.order_id,
           o.customer_id,
           DATE_TRUNC('month', o.ordered_at)           AS month,
           CAST(o.total_cents AS BIGINT) / 100.0        AS total,
           o.meta -> 'items'                           AS items,
           jsonb_array_length(o.meta -> 'items')       AS item_count,
           o.currency::VARCHAR(3)                      AS currency,
           CASE
               WHEN o.total_cents > 100000 THEN 'large'
               WHEN o.total_cents BETWEEN 10000 AND 100000 THEN 'medium'
               ELSE 'small'
           END                                         AS size_band
    FROM sales.orders o
    JOIN active_customers ac ON ac.customer_id = o.customer_id
    WHERE o.ordered_at < :report_end
      AND o.refunded = FALSE
      /* exclude internal transfers: they inflate totals
         and are reconciled by finance separately */
      AND o.channel NOT LIKE 'internal%'
),
monthly AS (
    SELECT oe.customer_id,
           oe.month,
           SUM(oe.total)                                   AS revenue,
           COUNT(*)                                        AS orders,
           AVG(oe.item_count)::INT                         AS avg_items,
           ROW_NUMBER() OVER (PARTITION BY oe.customer_id ORDER BY oe.month DESC) AS rn,
           LAG(SUM(oe.total)) OVER w                       AS prev_revenue,
           SUM(SUM(oe.total)) OVER (w ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running
    FROM orders_enriched oe
    GROUP BY oe.customer_id, oe.month
    WINDOW w AS (PARTITION BY oe.customer_id ORDER BY oe.month)
)
SELECT ac.customer_id,
       ac.tier,
       ac.country,
       m.month,
       m.revenue,
       m.orders,
       ROUND(100.0 * (m.revenue - m.prev_revenue) / NULLIF(m.prev_revenue, 0), 1) AS growth_pct,
       m.running,
       CASE WHEN ac.is_vip AND m.revenue > ac.credit_limit THEN 'review' ELSE NULL END AS flag,
       (SELECT string_agg(rt.name, ' › ' ORDER BY rt.depth)
          FROM region_tree rt
         WHERE rt.region_id = ANY (SELECT region_id FROM sales.customer_regions cr WHERE cr.customer_id = ac.customer_id)) AS regions
FROM active_customers ac
JOIN monthly m ON m.customer_id = ac.customer_id AND m.rn <= 12
LEFT JOIN LATERAL (
    SELECT jsonb_object_agg(k, v) AS overrides
    FROM jsonb_each_text(ac.profile -> 'overrides') AS t(k, v)
    WHERE v <> ''
) ov ON TRUE
WHERE m.revenue > 0
  AND (ac.tier = 'gold' OR ac.tier = 'platinum' OR ac.is_vip)
ORDER BY m.revenue DESC NULLS LAST, ac.customer_id
LIMIT 500 OFFSET 0;

-- ---------------------------------------------------------------------------
-- T-SQL section (SQL Server dialect): variables, temp tables, batches
-- ---------------------------------------------------------------------------
DECLARE @since DATE = DATEADD(DAY, -30, CAST(GETDATE() AS DATE));
DECLARE @limit INT = 250, @verbose BIT = 1;

IF OBJECT_ID('tempdb..#top_accounts') IS NOT NULL DROP TABLE #top_accounts;

SELECT TOP (@limit)
       a.AccountId,
       a.[Display Name]                         AS name,
       SUM(t.Amount)                            AS volume,
       COUNT_BIG(*)                             AS txn_count,
       MAX(t.PostedAt)                          AS last_seen,
       IIF(SUM(t.Amount) > 1e6, N'whale', N'regular') AS segment
INTO #top_accounts
FROM dbo.Accounts AS a WITH (NOLOCK)
INNER JOIN ledger.Transactions t ON t.AccountId = a.AccountId
WHERE t.PostedAt >= @since
  AND t.Status <> N'void'
  AND t.Amount IS NOT NULL
  AND a.Region LIKE N'EU-%'
GROUP BY a.AccountId, a.[Display Name]
HAVING SUM(t.Amount) > 0x0F4240              -- 1,000,000 in hex
ORDER BY volume DESC;

IF @@ROWCOUNT = 0 AND @verbose = 1
    PRINT N'No accounts matched since ' + CONVERT(NVARCHAR(10), @since, 120);
GO

MERGE INTO dbo.AccountSegments AS target
USING #top_accounts AS source
   ON target.AccountId = source.AccountId
WHEN MATCHED AND target.Segment <> source.segment THEN
    UPDATE SET target.Segment = source.segment, target.UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (AccountId, Segment, UpdatedAt) VALUES (source.AccountId, source.segment, SYSUTCDATETIME())
OUTPUT $action, inserted.AccountId;
GO

-- ---------------------------------------------------------------------------
-- MySQL / SQLite style: ? and named params, backticks, JSON paths
-- ---------------------------------------------------------------------------
UPDATE `inventory`.`stock_levels` sl
   SET sl.qty_on_hand = sl.qty_on_hand - ?,
       sl.reserved    = GREATEST(sl.reserved - ?, 0),
       sl.attrs       = JSON_SET(sl.attrs, '$.last_move', NOW())
 WHERE sl.sku = :sku
   AND sl.warehouse_id = @warehouse
   AND JSON_EXTRACT(sl.attrs, '$.locked') = FALSE;

INSERT INTO audit_log (actor, action, payload, at)
VALUES ('svc-inventory', 'stock.decrement', json_object('sku', :sku, 'qty', ?), CURRENT_TIMESTAMP);

-- Oracle: bind variables, NVL, DECODE, connect by
SELECT LPAD(' ', 2 * (LEVEL - 1)) || e.last_name AS org_chart,
       NVL(e.commission_pct, 0) * e.salary        AS commission,
       DECODE(e.job_id, 'SA_REP', 'Sales', 'IT_PROG', 'Engineering', 'Other') AS dept_kind
FROM hr.employees e
START WITH e.manager_id IS NULL
CONNECT BY PRIOR e.employee_id = e.manager_id
ORDER SIBLINGS BY e.last_name;

-- DuckDB: list/struct functions, QUALIFY
SELECT region, product, revenue,
       list_aggregate([revenue, cost], 'sum')   AS combined,
       {'k': region, 'v': revenue}              AS kv
FROM read_parquet('exports/sales_2026_*.parquet')
QUALIFY ROW_NUMBER() OVER (PARTITION BY region ORDER BY revenue DESC) <= 3;

/* Edge cases:
   - a string containing -- is not a comment: '--not a comment'
   - a string with an escaped quote: 'it''s fine'
   - E'escapes\tin\npostgres' and $$dollar quoted -- text$$
*/
SELECT '--not a comment' AS s1, 'it''s fine' AS s2, E'tab\there' AS s3, $$dollar -- text$$ AS s4,
       NULL::JSONB AS empty_doc, TRUE AND NOT FALSE AS logic, 3.14e-2 AS sci, -42 AS neg,
       x'DEADBEEF' AS blob, INTERVAL '3 days' AS span, '2026-11-24'::DATE AS d;
