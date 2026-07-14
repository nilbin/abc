using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Tam.EntityFrameworkCore;

/// <summary>
/// Typed JSON access over the ExtensionData column (docs/15's "real JSON translation"): the
/// path from string-containment matching to real predicates. Call sites pass the converted
/// column via the (string)(object) double-cast; the translations read the raw column and
/// extract per provider — SQLite <c>json_extract</c>, PostgreSQL <c>jsonb_extract_path_text</c>
/// (with a numeric cast for <see cref="JsonNumber"/>). The key is always a constant from the
/// declared overlay — user input only ever reaches the VALUE side of a comparison.
/// </summary>
public static class TamDbFunctions
{
    /// <summary>Extension value as text (strings, ISO dates — ordinal range-safe).</summary>
    public static string? JsonValue(string json, string key)
        => throw new InvalidOperationException("Query-only function.");

    /// <summary>Extension value as a number (numeric equality and ranges). Double, not
    /// decimal: SQLite compares REAL natively while decimal degrades to TEXT.</summary>
    public static double? JsonNumber(string json, string key)
        => throw new InvalidOperationException("Query-only function.");

    internal static void Register(ModelBuilder modelBuilder, bool isNpgsql)
    {
        modelBuilder.HasDbFunction(() => JsonValue(default!, default!))
            .HasTranslation(args => Extract(args, isNpgsql));

        modelBuilder.HasDbFunction(() => JsonNumber(default!, default!))
            .HasTranslation(args => isNpgsql
                // text -> numeric needs an explicit cast on PostgreSQL…
                ? new SqlUnaryExpression(
                    ExpressionType.Convert, Extract(args, isNpgsql: true),
                    typeof(double), new DoubleTypeMapping("double precision"))
                // …while SQLite's json_extract already yields numeric affinity for JSON numbers.
                // (SqlExpression type must be the non-nullable value type even for nullable SQL.)
                : new SqlFunctionExpression(
                    "json_extract", [Unwrap(args[0]), SqlitePath(args[1])],
                    nullable: true, argumentsPropagateNullability: [true, true],
                    typeof(double), new DoubleTypeMapping("REAL")));
    }

    private static SqlExpression Extract(IReadOnlyList<SqlExpression> args, bool isNpgsql) =>
        isNpgsql
            ? new SqlFunctionExpression(
                "jsonb_extract_path_text", [Unwrap(args[0]), args[1]],
                nullable: true, argumentsPropagateNullability: [true, true],
                typeof(string), new StringTypeMapping("text", System.Data.DbType.String))
            : new SqlFunctionExpression(
                "json_extract", [Unwrap(args[0]), SqlitePath(args[1])],
                nullable: true, argumentsPropagateNullability: [true, true],
                typeof(string), new StringTypeMapping("TEXT", System.Data.DbType.String));

    /// <summary>
    /// The call site reaches the converted column through the (string)(object) double-cast;
    /// by translation time that may survive as CAST(col AS text), which would defeat jsonb
    /// functions on PostgreSQL — peel converts back to the raw column reference.
    /// </summary>
    private static SqlExpression Unwrap(SqlExpression expression) =>
        expression is SqlUnaryExpression { OperatorType: ExpressionType.Convert } convert
            ? Unwrap(convert.Operand)
            : expression;

    /// <summary>SQLite addresses members by JSONPath — rewrite the constant key to "$.key".</summary>
    private static SqlExpression SqlitePath(SqlExpression key) =>
        key is SqlConstantExpression { Value: string k } constant
            ? new SqlConstantExpression($"$.{k}", constant.TypeMapping)
            : key;
}
