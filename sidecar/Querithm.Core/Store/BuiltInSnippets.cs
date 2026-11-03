using Querithm.Contract;

namespace Querithm.Core.Store;

/// <summary>The read-only snippets every install has, per dialect (Monaco snippet syntax: <c>${n:hint}</c>; a literal <c>$</c> is <c>\$</c>).</summary>
public static class BuiltInSnippets
{
    public const string IdPrefix = "builtin:";

    static Snippet S(Dialect d, string prefix, string title, string body, params string[] tags) => new()
    {
        Id = $"{IdPrefix}{EnumText.Of(d)}:{prefix}", Title = title, Prefix = prefix, Body = body, Dialect = d, Tags = tags, BuiltIn = true,
    };

    public static readonly Snippet[] All =
    [
        S(Dialect.Tsql, "selt", "SELECT TOP", "SELECT TOP (${1:100}) ${2:*}\nFROM ${3:dbo.table}\nWHERE ${4:1 = 1};$0", "query"),
        S(Dialect.Tsql, "updw", "UPDATE with WHERE", "UPDATE ${1:dbo.table}\nSET ${2:column} = ${3:value}\nWHERE ${4:id} = ${5:@id};$0", "dml"),
        S(Dialect.Tsql, "trycatch", "TRY / CATCH with rollback",
            "BEGIN TRY\n    BEGIN TRANSACTION;\n    $0\n    COMMIT TRANSACTION;\nEND TRY\nBEGIN CATCH\n    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\n    THROW;\nEND CATCH;", "transaction"),
        S(Dialect.Tsql, "cte", "Common table expression", "WITH ${1:cte} AS (\n    SELECT ${2:*}\n    FROM ${3:dbo.table}\n)\nSELECT *\nFROM ${1:cte};$0", "query"),
        S(Dialect.Tsql, "proc", "CREATE OR ALTER PROCEDURE",
            "CREATE OR ALTER PROCEDURE ${1:dbo.usp_name}\n    @${2:param} ${3:int}\nAS\nBEGIN\n    SET NOCOUNT ON;\n    $0\nEND;", "ddl"),
        S(Dialect.Pg, "sell", "SELECT … LIMIT", "SELECT ${1:*}\nFROM ${2:public.table}\nWHERE ${3:true}\nLIMIT ${4:100};$0", "query"),
        S(Dialect.Pg, "upsert", "INSERT … ON CONFLICT DO UPDATE",
            "INSERT INTO ${1:public.table} (${2:id}, ${3:col})\nVALUES (${4:1}, ${5:'x'})\nON CONFLICT (${2:id}) DO UPDATE SET ${3:col} = EXCLUDED.${3:col};$0", "dml"),
        S(Dialect.Pg, "cte", "Common table expression", "WITH ${1:cte} AS (\n    SELECT ${2:*}\n    FROM ${3:public.table}\n)\nSELECT *\nFROM ${1:cte};$0", "query"),
        S(Dialect.Pg, "plfn", "CREATE OR REPLACE FUNCTION (PL/pgSQL)",
            "CREATE OR REPLACE FUNCTION ${1:public.fn_name}(${2:p_id int})\nRETURNS ${3:int}\nLANGUAGE plpgsql AS \\$\\$\nDECLARE\n    ${4:v_result int};\nBEGIN\n    $0\n    RETURN ${5:v_result};\nEND\n\\$\\$;", "ddl"),
        S(Dialect.Pg, "doblock", "DO block", "DO \\$\\$\nBEGIN\n    $0\nEND\n\\$\\$;", "plpgsql"),
    ];
}
