-- T1 corpus, T-SQL: every Monarch rule of tsqlLanguage() fires at least once (highlightGolden.test.ts checks it).
DECLARE @#legacy INT = 1, @$cash MONEY = $12.50, @n FLOAT = .5e1, @d DATETIME2 = SYSDATETIME();
SELECT ##global_temp.id, #local.[col]]name], "quoted""name", N'it''s', '', 0x, 0xFF, 1.5E+3, 2e-1, 10.
FROM [dbo].[My Table] AS t WITH (NOLOCK)
WHERE x <> 1 AND y != 2 AND z !< 3 AND w !> 4 AND v >= 5 AND (u & 1) | 2 ^ ~3 = 0 AND s % 2 = 1;
SET @n += 1; SET @n -= 1; SET @n *= 2; SET @n /= 2; SET @n %= 2;
/* outer /* nested */ still * comment / here */
/* a block comment
   across lines * with a star
*/ SELECT 1;
SELECT @@SPID, @@ERROR, @@TRANCOUNT, @since, @@ROWCOUNT + 1;
EXEC dbo.usp_Refresh @Mode = N'full', @Force = 1;
GO
GO 5
  go
SELECT CAST(x AS VARCHAR(MAX)), TRY_CONVERT(INT, y), COUNT_BIG(*), STRING_AGG(z, ',') FROM dbo.t;
