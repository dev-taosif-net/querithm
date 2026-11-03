namespace Querithm.Intelligence.TSql.Completion;

/// <summary>Built-in T-SQL functions (signatures, return types), global variables and data types.</summary>
static class Builtins
{
    public sealed record Fn(string Name, string[] Params, string Returns, string? Doc = null)
    {
        public string Label => $"{Name}({string.Join(", ", Params)}) → {Returns}";
    }

    public static readonly Dictionary<string, Fn> Functions = new Fn[]
    {
        new("DATEADD", ["datepart", "number int", "date"], "type of date", "Adds number datepart units to date."),
        new("DATEDIFF", ["datepart", "startdate", "enddate"], "int", "Count of datepart boundaries crossed between startdate and enddate."),
        new("DATEDIFF_BIG", ["datepart", "startdate", "enddate"], "bigint"),
        new("DATEPART", ["datepart", "date"], "int"),
        new("DATENAME", ["datepart", "date"], "nvarchar(30)"),
        new("DATEFROMPARTS", ["year int", "month int", "day int"], "date"),
        new("EOMONTH", ["start_date", "month_to_add int = 0"], "date"),
        new("YEAR", ["date"], "int"),
        new("MONTH", ["date"], "int"),
        new("DAY", ["date"], "int"),
        new("GETDATE", [], "datetime"),
        new("GETUTCDATE", [], "datetime"),
        new("SYSDATETIME", [], "datetime2(7)"),
        new("SYSUTCDATETIME", [], "datetime2(7)"),
        new("SYSDATETIMEOFFSET", [], "datetimeoffset(7)"),
        new("CONVERT", ["data_type[(length)]", "expression", "style int = 0"], "data_type", "Converts expression to data_type using an optional style."),
        new("TRY_CONVERT", ["data_type[(length)]", "expression", "style int = 0"], "data_type or NULL"),
        new("CAST", ["expression AS data_type[(length)]"], "data_type"),
        new("TRY_CAST", ["expression AS data_type[(length)]"], "data_type or NULL"),
        new("PARSE", ["string_value AS data_type [USING culture]"], "data_type"),
        new("ISNULL", ["check_expression", "replacement_value"], "type of check_expression", "Replaces NULL with replacement_value."),
        new("COALESCE", ["expression1", "expression2", "..."], "highest-precedence argument type", "First non-NULL argument."),
        new("NULLIF", ["expression1", "expression2"], "type of expression1"),
        new("IIF", ["boolean_expression", "true_value", "false_value"], "highest-precedence of true_value/false_value"),
        new("CHOOSE", ["index int", "value1", "value2", "..."], "highest-precedence value type"),
        new("SUBSTRING", ["expression", "start int", "length int"], "type of expression"),
        new("CHARINDEX", ["expressionToFind", "expressionToSearch", "start_location int = 0"], "int"),
        new("PATINDEX", ["'%pattern%'", "expression"], "int"),
        new("LEFT", ["character_expression", "integer_expression int"], "varchar/nvarchar"),
        new("RIGHT", ["character_expression", "integer_expression int"], "varchar/nvarchar"),
        new("LEN", ["string_expression"], "int", "Number of characters, excluding trailing spaces."),
        new("DATALENGTH", ["expression"], "int"),
        new("REPLACE", ["string_expression", "string_pattern", "string_replacement"], "type of string_expression"),
        new("REPLICATE", ["string_expression", "integer_expression int"], "type of string_expression"),
        new("STUFF", ["character_expression", "start int", "length int", "replace_with_expression"], "type of character_expression"),
        new("UPPER", ["character_expression"], "type of character_expression"),
        new("LOWER", ["character_expression"], "type of character_expression"),
        new("LTRIM", ["character_expression"], "type of character_expression"),
        new("RTRIM", ["character_expression"], "type of character_expression"),
        new("TRIM", ["[characters FROM] string"], "type of string"),
        new("REVERSE", ["string_expression"], "type of string_expression"),
        new("CONCAT", ["string_value1", "string_value2", "..."], "nvarchar/varchar"),
        new("CONCAT_WS", ["separator", "argument1", "argument2", "..."], "nvarchar/varchar"),
        new("STRING_AGG", ["expression", "separator"], "nvarchar(max)/varchar(max)", "Concatenates values with separator; WITHIN GROUP (ORDER BY ...) optional."),
        new("STRING_SPLIT", ["string", "separator", "enable_ordinal bit = 0"], "table(value, ordinal)"),
        new("FORMAT", ["value", "format nvarchar", "culture nvarchar = NULL"], "nvarchar(4000)"),
        new("STR", ["float_expression", "length int = 10", "decimal int = 0"], "varchar"),
        new("ROUND", ["numeric_expression", "length int", "function int = 0"], "type of numeric_expression"),
        new("ABS", ["numeric_expression"], "type of numeric_expression"),
        new("CEILING", ["numeric_expression"], "type of numeric_expression"),
        new("FLOOR", ["numeric_expression"], "type of numeric_expression"),
        new("POWER", ["float_expression", "y"], "type of float_expression"),
        new("SQRT", ["float_expression"], "float"),
        new("COUNT", ["{ * | [ALL | DISTINCT] expression }"], "int"),
        new("COUNT_BIG", ["{ * | [ALL | DISTINCT] expression }"], "bigint"),
        new("SUM", ["[ALL | DISTINCT] expression"], "type of expression"),
        new("AVG", ["[ALL | DISTINCT] expression"], "type of expression"),
        new("MIN", ["expression"], "type of expression"),
        new("MAX", ["expression"], "type of expression"),
        new("ROW_NUMBER", [], "bigint", "ROW_NUMBER() OVER ([PARTITION BY ...] ORDER BY ...)"),
        new("RANK", [], "bigint"),
        new("DENSE_RANK", [], "bigint"),
        new("NTILE", ["integer_expression int"], "bigint"),
        new("LAG", ["scalar_expression", "offset int = 1", "default = NULL"], "type of scalar_expression"),
        new("LEAD", ["scalar_expression", "offset int = 1", "default = NULL"], "type of scalar_expression"),
        new("NEWID", [], "uniqueidentifier"),
        new("SCOPE_IDENTITY", [], "numeric(38,0)"),
        new("OBJECT_ID", ["'object_name'", "'object_type' = NULL"], "int"),
        new("OBJECT_NAME", ["object_id int", "database_id int = NULL"], "sysname"),
        new("DB_NAME", ["database_id int = NULL"], "nvarchar(128)"),
        new("JSON_VALUE", ["expression", "path"], "nvarchar(4000)"),
        new("JSON_QUERY", ["expression", "path = '$'"], "nvarchar(max)"),
        new("ISJSON", ["expression"], "int"),
        new("HASHBYTES", ["'algorithm'", "input"], "varbinary(8000)"),
        new("GREATEST", ["expression1", "expression2", "..."], "highest-precedence argument type"),
        new("LEAST", ["expression1", "expression2", "..."], "highest-precedence argument type"),
    }.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Result type of a built-in call given its argument types (null when unknown).</summary>
    public static string? ReturnType(string name, Func<int, string?> arg) => name.ToUpperInvariant() switch
    {
        "COUNT" or "LEN" or "DATALENGTH" or "CHARINDEX" or "PATINDEX" or "DATEDIFF" or "DATEPART" or "YEAR" or "MONTH" or "DAY"
            or "ASCII" or "UNICODE" or "OBJECT_ID" or "ISJSON" or "ISNUMERIC" or "CHECKSUM" or "BINARY_CHECKSUM" or "@@ROWCOUNT" => "int",
        "COUNT_BIG" or "DATEDIFF_BIG" or "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "NTILE" => "bigint",
        "SUM" or "AVG" or "MIN" or "MAX" or "ISNULL" or "UPPER" or "LOWER" or "LTRIM" or "RTRIM" or "TRIM" or "SUBSTRING" or "REPLACE"
            or "STUFF" or "REVERSE" or "REPLICATE" or "ROUND" or "ABS" or "CEILING" or "FLOOR" or "LAG" or "LEAD" or "FIRST_VALUE"
            or "LAST_VALUE" or "GREATEST" or "LEAST" or "POWER" or "TRANSLATE" => arg(0),
        "DATEADD" => arg(2),
        "CHOOSE" => arg(1),
        "STRING_AGG" => arg(0) is { } t ? (t.StartsWith("n", StringComparison.OrdinalIgnoreCase) ? "nvarchar(max)" : "varchar(max)") : "nvarchar(max)",
        "CONCAT" or "CONCAT_WS" => "nvarchar(4000)",
        "FORMAT" => "nvarchar(4000)",
        "DATENAME" => "nvarchar(30)",
        "STR" => "varchar(10)",
        "GETDATE" or "GETUTCDATE" or "CURRENT_TIMESTAMP" => "datetime",
        "SYSDATETIME" or "SYSUTCDATETIME" => "datetime2(7)",
        "SYSDATETIMEOFFSET" => "datetimeoffset(7)",
        "DATEFROMPARTS" or "EOMONTH" => "date",
        "NEWID" or "NEWSEQUENTIALID" => "uniqueidentifier",
        "SCOPE_IDENTITY" or "IDENT_CURRENT" => "numeric(38,0)",
        "OBJECT_NAME" or "DB_NAME" or "SCHEMA_NAME" or "SUSER_SNAME" or "USER_NAME" => "nvarchar(128)",
        "JSON_VALUE" => "nvarchar(4000)",
        "JSON_QUERY" => "nvarchar(max)",
        "HASHBYTES" => "varbinary(8000)",
        "SQRT" or "EXP" or "LOG" or "LOG10" or "RAND" or "PI" or "SIN" or "COS" or "TAN" or "STDEV" or "STDEVP" or "VAR" or "VARP" => "float",
        _ => null,
    };

    public static readonly (string Name, string Type)[] GlobalVariables =
    [
        ("@@ROWCOUNT", "int"), ("@@ERROR", "int"), ("@@IDENTITY", "numeric(38,0)"), ("@@TRANCOUNT", "int"), ("@@FETCH_STATUS", "int"),
        ("@@SPID", "smallint"), ("@@SERVERNAME", "nvarchar(128)"), ("@@VERSION", "nvarchar(300)"), ("@@PROCID", "int"),
        ("@@DATEFIRST", "tinyint"), ("@@NESTLEVEL", "int"),
    ];

    public static string? GlobalVariableType(string name)
    {
        foreach (var (n, t) in GlobalVariables) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>Type names offered after DECLARE @x, in parameter lists, CAST(... AS, CONVERT( and column definitions.</summary>
    public static readonly (string Label, string? Insert, string Detail)[] Types =
    [
        ("int", null, "4-byte integer"), ("bigint", null, "8-byte integer"), ("smallint", null, "2-byte integer"), ("tinyint", null, "0..255"),
        ("bit", null, "0 / 1 / NULL"), ("decimal", "decimal(${1:18}, ${2:2})", "exact numeric (p, s)"), ("numeric", "numeric(${1:18}, ${2:2})", "exact numeric (p, s)"),
        ("money", null, "currency"), ("smallmoney", null, "currency"), ("float", null, "approximate numeric"), ("real", null, "approximate numeric"),
        ("date", null, "date"), ("datetime", null, "date and time (3.33 ms)"), ("datetime2", "datetime2(${1:7})", "date and time (100 ns)"),
        ("datetimeoffset", "datetimeoffset(${1:7})", "date, time and offset"), ("smalldatetime", null, "date and time (1 min)"), ("time", "time(${1:7})", "time of day"),
        ("char", "char(${1:10})", "fixed-length ANSI"), ("varchar", "varchar(${1:50})", "variable-length ANSI"), ("varchar(max)", null, "large ANSI text"),
        ("nchar", "nchar(${1:10})", "fixed-length Unicode"), ("nvarchar", "nvarchar(${1:50})", "variable-length Unicode"), ("nvarchar(max)", null, "large Unicode text"),
        ("binary", "binary(${1:16})", "fixed-length binary"), ("varbinary", "varbinary(${1:50})", "variable-length binary"), ("varbinary(max)", null, "large binary"),
        ("uniqueidentifier", null, "GUID"), ("xml", null, "XML document"), ("sql_variant", null, "any base type"), ("sysname", null, "nvarchar(128) NOT NULL"),
        ("hierarchyid", null, "tree position"), ("geography", null, "spatial (round earth)"), ("geometry", null, "spatial (flat)"), ("rowversion", null, "row version"),
    ];
}
