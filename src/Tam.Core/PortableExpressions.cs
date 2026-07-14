using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Tam;

/// <summary>
/// The deliberately constrained portable expression AST (docs/05). One AST, two evaluators:
/// this file (server) and @tam/core (client). Compiled rules are lowered from LINQ expression
/// trees at startup; tenant rules are authored as AST data by the registry.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(PxConst), "const")]
[JsonDerivedType(typeof(PxField), "field")]
[JsonDerivedType(typeof(PxBinary), "bin")]
[JsonDerivedType(typeof(PxUnary), "un")]
public abstract record Px
{
    public abstract object? Evaluate(Func<string, object?> field);

    public abstract IEnumerable<string> Fields();
}

public sealed record PxConst(object? V) : Px
{
    public override object? Evaluate(Func<string, object?> field) => V;

    public override IEnumerable<string> Fields() => [];
}

public sealed record PxField(string F) : Px
{
    public override object? Evaluate(Func<string, object?> field) => field(F);

    public override IEnumerable<string> Fields() => [F];
}

public sealed record PxUnary(string Op, Px X) : Px
{
    public override object? Evaluate(Func<string, object?> field) => Op switch
    {
        "not" => !PxBinary.Truthy(X.Evaluate(field)),
        "isNull" => X.Evaluate(field) is null,
        "isNotNull" => X.Evaluate(field) is not null,
        _ => throw new NotSupportedException($"Portable unary op '{Op}'."),
    };

    public override IEnumerable<string> Fields() => X.Fields();
}

public sealed record PxBinary(string Op, Px L, Px R) : Px
{
    public override object? Evaluate(Func<string, object?> field)
    {
        if (Op == "and") return Truthy(L.Evaluate(field)) && Truthy(R.Evaluate(field));
        if (Op == "or") return Truthy(L.Evaluate(field)) || Truthy(R.Evaluate(field));

        var l = Normalize(L.Evaluate(field));
        var r = Normalize(R.Evaluate(field));
        return Op switch
        {
            "eq" => LooseEquals(l, r),
            "ne" => !LooseEquals(l, r),
            "gt" => Compare(l, r) > 0,
            "ge" => Compare(l, r) >= 0,
            "lt" => Compare(l, r) < 0,
            "le" => Compare(l, r) <= 0,
            _ => throw new NotSupportedException($"Portable binary op '{Op}'."),
        };
    }

    public override IEnumerable<string> Fields() => [.. L.Fields(), .. R.Fields()];

    internal static bool Truthy(object? v) => v is true;

    private static object? Normalize(object? v)
    {
        v = ValueWrapper.Unwrap(v);
        return v switch
        {
            null => null,
            Enum e => e.ToString(),
            int i => (decimal)i,
            long l => (decimal)l,
            double d => (decimal)d,
            System.Text.Json.JsonElement je => je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Number => je.GetDecimal(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                _ => je.ToString(),
            },
            _ => v,
        };
    }

    private static bool LooseEquals(object? l, object? r)
    {
        if (l is string ls && r is string rs) return string.Equals(ls, rs, StringComparison.Ordinal);
        return Equals(l, r);
    }

    private static int Compare(object? l, object? r) => (l, r) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (decimal a, decimal b) => a.CompareTo(b),
        (string a, string b) => string.CompareOrdinal(a, b),
        (IComparable a, _) => a.CompareTo(r),
        _ => throw new NotSupportedException("Portable compare on non-comparable values."),
    };
}

/// <summary>
/// Lowers a constrained LINQ expression to the portable AST. Lowers completely or throws with
/// the offending node — never partial translation (review-notes risk #1).
/// </summary>
public static class PortableExpression
{
    public static Px Lower<TInput>(Expression<Func<TInput, bool>> rule) => LowerNode(rule.Body, rule.Parameters[0]);

    private static Px LowerNode(Expression node, ParameterExpression input)
    {
        switch (node)
        {
            case BinaryExpression b when Map(b.NodeType) is { } op:
                return new PxBinary(op, LowerNode(b.Left, input), LowerNode(b.Right, input));

            case UnaryExpression { NodeType: ExpressionType.Not } u:
                return new PxUnary("not", LowerNode(u.Operand, input));

            case UnaryExpression { NodeType: ExpressionType.Convert } c:
                return LowerNode(c.Operand, input);

            case MemberExpression m when IsInputMember(m, input, out var path):
                return new PxField(path);

            case MemberExpression { Member.Name: "HasValue" } hv when hv.Expression is MemberExpression inner
                    && IsInputMember(inner, input, out var innerPath):
                return new PxUnary("isNotNull", new PxField(innerPath));

            case ConstantExpression c:
                return Const(c.Value);

            case MemberExpression closure:
                return Const(Expression.Lambda(closure).Compile().DynamicInvoke());

            default:
                throw new NotSupportedException(
                    $"PORT001: expression node '{node.NodeType}' ({node}) is outside the portable subset. " +
                    "Use a [ServerDerivation] for logic the portable AST cannot express.");
        }
    }

    private static Px Const(object? value)
    {
        value = ValueWrapper.Unwrap(value);
        return new PxConst(value is Enum e ? e.ToString() : value);
    }

    private static bool IsInputMember(MemberExpression m, ParameterExpression input, out string path)
    {
        // input.Field or input.Field.Value (semantic wrapper unwrap)
        if (m.Expression == input)
        {
            path = Naming.Camel(m.Member.Name);
            return true;
        }
        if (m.Member.Name == "Value" && m.Expression is MemberExpression parent && parent.Expression == input)
        {
            path = Naming.Camel(parent.Member.Name);
            return true;
        }
        path = string.Empty;
        return false;
    }

    private static string? Map(ExpressionType type) => type switch
    {
        ExpressionType.Equal => "eq",
        ExpressionType.NotEqual => "ne",
        ExpressionType.GreaterThan => "gt",
        ExpressionType.GreaterThanOrEqual => "ge",
        ExpressionType.LessThan => "lt",
        ExpressionType.LessThanOrEqual => "le",
        ExpressionType.AndAlso => "and",
        ExpressionType.OrElse => "or",
        _ => null,
    };
}
