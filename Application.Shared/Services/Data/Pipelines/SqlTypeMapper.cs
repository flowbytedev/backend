using Application.Shared.Enums;

namespace Application.Shared.Services.Data.Pipelines;

/// <summary>
/// Maps a DuckDB type name onto the nearest equivalent in each destination engine, for create-if-missing DDL.
/// <para>
/// Deliberately conservative: where two candidate types differ in precision, this picks the wider one. A
/// pipeline that creates a column too narrow fails at load time on real data, which is a far worse outcome
/// than a column that is roomier than it needed to be. The same reasoning makes every string column the
/// engine's unbounded text type rather than a guessed length — DuckDB's VARCHAR carries no length, so any
/// number here would be invented.
/// </para>
/// </summary>
public static class SqlTypeMapper
{
    /// <summary>The destination column type for a DuckDB type, or null when there is no safe mapping.</summary>
    public static string? For(DataSourceType target, string duckdbType)
    {
        var normalized = Normalize(duckdbType);

        return target switch
        {
            DataSourceType.SQLServer => normalized switch
            {
                "BOOLEAN" => "BIT",
                "TINYINT" => "TINYINT",
                "SMALLINT" => "SMALLINT",
                "INTEGER" => "INT",
                "BIGINT" => "BIGINT",
                "HUGEINT" => "DECIMAL(38,0)",
                "FLOAT" => "REAL",
                "DOUBLE" => "FLOAT",
                "DECIMAL" => "DECIMAL(38,10)",
                "DATE" => "DATE",
                "TIME" => "TIME",
                "TIMESTAMP" => "DATETIME2(7)",
                "TIMESTAMPTZ" => "DATETIMEOFFSET(7)",
                "UUID" => "UNIQUEIDENTIFIER",
                "BLOB" => "VARBINARY(MAX)",
                "JSON" or "VARCHAR" => "NVARCHAR(MAX)",
                _ => null
            },

            DataSourceType.PostgreSQL => normalized switch
            {
                "BOOLEAN" => "boolean",
                "TINYINT" or "SMALLINT" => "smallint",
                "INTEGER" => "integer",
                "BIGINT" => "bigint",
                "HUGEINT" => "numeric(38,0)",
                "FLOAT" => "real",
                "DOUBLE" => "double precision",
                "DECIMAL" => "numeric(38,10)",
                "DATE" => "date",
                "TIME" => "time",
                "TIMESTAMP" => "timestamp",
                "TIMESTAMPTZ" => "timestamptz",
                "UUID" => "uuid",
                "BLOB" => "bytea",
                "JSON" => "jsonb",
                "VARCHAR" => "text",
                _ => null
            },

            DataSourceType.MySQL => normalized switch
            {
                "BOOLEAN" => "TINYINT(1)",
                "TINYINT" => "TINYINT",
                "SMALLINT" => "SMALLINT",
                "INTEGER" => "INT",
                "BIGINT" => "BIGINT",
                "HUGEINT" => "DECIMAL(38,0)",
                "FLOAT" => "FLOAT",
                "DOUBLE" => "DOUBLE",
                "DECIMAL" => "DECIMAL(38,10)",
                "DATE" => "DATE",
                "TIME" => "TIME",
                "TIMESTAMP" or "TIMESTAMPTZ" => "DATETIME(6)",
                "UUID" => "CHAR(36)",
                "BLOB" => "LONGBLOB",
                "JSON" => "JSON",
                // Not VARCHAR: MySQL needs a length, and any length chosen here would be a guess that
                // truncates real data. LONGTEXT cannot be indexed without a prefix, which is the honest
                // trade for not knowing the width.
                "VARCHAR" => "LONGTEXT",
                _ => null
            },

            DataSourceType.ClickHouse => normalized switch
            {
                // Nullable(...) throughout: a pipeline's intermediate relations carry no NOT NULL
                // information, so assuming non-null would reject perfectly good rows.
                "BOOLEAN" => "Nullable(UInt8)",
                "TINYINT" => "Nullable(Int8)",
                "SMALLINT" => "Nullable(Int16)",
                "INTEGER" => "Nullable(Int32)",
                "BIGINT" => "Nullable(Int64)",
                "HUGEINT" => "Nullable(Int128)",
                "FLOAT" => "Nullable(Float32)",
                "DOUBLE" => "Nullable(Float64)",
                "DECIMAL" => "Nullable(Decimal(38,10))",
                "DATE" => "Nullable(Date32)",
                "TIME" => "Nullable(String)",
                "TIMESTAMP" or "TIMESTAMPTZ" => "Nullable(DateTime64(3))",
                "UUID" => "Nullable(UUID)",
                "JSON" or "BLOB" or "VARCHAR" => "Nullable(String)",
                _ => null
            },

            _ => null
        };
    }

    /// <summary>
    /// Reduces a DuckDB type name to the family this mapper understands. Parameters are dropped
    /// (<c>DECIMAL(18,2)</c> becomes <c>DECIMAL</c>) because the mapping widens anyway, and a composite type
    /// returns itself so <see cref="For"/> can decline it rather than silently producing text.
    /// </summary>
    private static string Normalize(string duckdbType)
    {
        var type = (duckdbType ?? string.Empty).Trim().ToUpperInvariant();

        var paren = type.IndexOf('(');
        if (paren > 0) type = type[..paren];

        return type switch
        {
            "TEXT" or "STRING" or "CHAR" or "BPCHAR" => "VARCHAR",
            "INT" or "INT4" or "INTEGER" or "SIGNED" => "INTEGER",
            "INT8" or "LONG" or "BIGINT" => "BIGINT",
            "INT2" or "SHORT" or "SMALLINT" => "SMALLINT",
            "INT1" or "TINYINT" => "TINYINT",
            "INT16" or "HUGEINT" => "HUGEINT",
            "BOOL" or "LOGICAL" or "BOOLEAN" => "BOOLEAN",
            "FLOAT4" or "REAL" or "FLOAT" => "FLOAT",
            "FLOAT8" or "DOUBLE" => "DOUBLE",
            "NUMERIC" or "DECIMAL" => "DECIMAL",
            "DATETIME" or "TIMESTAMP_NS" or "TIMESTAMP_MS" or "TIMESTAMP_S" or "TIMESTAMP" => "TIMESTAMP",
            "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ" => "TIMESTAMPTZ",
            "BYTEA" or "BINARY" or "VARBINARY" or "BLOB" => "BLOB",
            _ => type
        };
    }

    /// <summary>Quotes an identifier for the destination engine.</summary>
    public static string Quote(DataSourceType target, string identifier)
    {
        var name = identifier ?? string.Empty;

        return target switch
        {
            DataSourceType.SQLServer => "[" + name.Replace("]", "]]") + "]",
            DataSourceType.MySQL => "`" + name.Replace("`", "``") + "`",
            // PostgreSQL, ClickHouse and DuckDB all use double quotes.
            _ => "\"" + name.Replace("\"", "\"\"") + "\""
        };
    }

    /// <summary>Fully-qualified table name, with the schema only where the engine has one.</summary>
    public static string QualifiedTable(DataSourceType target, string? schema, string table)
    {
        // MySQL and ClickHouse treat the "schema" slot as the database itself, so a pipeline's schema field
        // is ignored for them rather than producing db.db.table.
        var useSchema = !string.IsNullOrWhiteSpace(schema)
                        && target is DataSourceType.SQLServer or DataSourceType.PostgreSQL;

        return useSchema
            ? $"{Quote(target, schema!)}.{Quote(target, table)}"
            : Quote(target, table);
    }
}
