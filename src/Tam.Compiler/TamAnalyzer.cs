using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Tam.Compiler;

/// <summary>
/// The build-time half of docs/12: model-shape and localization-coverage diagnostics.
/// What the runtime enforces as startup gates, this analyzer surfaces in the IDE and CI
/// as ordinary compiler errors — before the app ever boots.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TamAnalyzer : DiagnosticAnalyzer
{
    private static DiagnosticDescriptor Rule(string id, string title, string message) => new(
        id, title, message, "Tam",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Tam001 = Rule(
        "TAM001", "Operation without permission",
        "Operation '{0}' has no [Authorize] permission — every operation must declare one");

    public static readonly DiagnosticDescriptor Tam002 = Rule(
        "TAM002", "Operation without Input",
        "Operation '{0}' has no nested Input record");

    public static readonly DiagnosticDescriptor Tam003 = Rule(
        "TAM003", "Operation without Execute",
        "Operation '{0}' has no public static Execute method");

    public static readonly DiagnosticDescriptor L10n001 = Rule(
        "L10N001", "Missing locale key",
        "Key '{0}' is missing in default culture '{1}' (referenced by {2})");

    public static readonly DiagnosticDescriptor Edit001 = new(
        "EDIT001", "Consequential state exposed via change-set",
        "Operation '{0}' exposes enum member '{1}' as Change<T> — state transitions belong to an intent-specific operation (docs/03)",
        "Tam", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Tam004 = Rule(
        "TAM004", "Redundant tenant filter",
        "Tenant scoping is automatic for ITenantScoped entities via the global query filter — remove this 'TenantId ==' predicate. For a deliberate cross-tenant read (a background job), chain .IgnoreQueryFilters() and this warning goes away.");

    public static readonly DiagnosticDescriptor Tam006 = Rule(
        "TAM006", "Ownership scoping is incomplete",
        "{0}");

    public static readonly DiagnosticDescriptor Tam005 = Rule(
        "TAM005", "Widened query composes an implicitly-filtered source",
        "EF's IgnoreQueryFilters is query-wide: composing a widened source (InSubtree/WithInherited/IgnoreQueryFilters) strips the global tenant filter from EVERY source in this query. Scope this side explicitly — .InNode(tenant) for strict, .InScope(db, tenant) for the ambient (possibly subtree-widened) scope, or its own InSubtree/WithInherited (docs/27, the composition rule).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Tam001, Tam002, Tam003, L10n001, Edit001, Tam004, Tam005, Tam006];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var defaultCulture = "en";
            if (start.Options.AnalyzerConfigOptionsProvider.GlobalOptions
                .TryGetValue("build_property.TamDefaultCulture", out var configured)
                && !string.IsNullOrWhiteSpace(configured))
            {
                defaultCulture = configured.Trim();
            }

            var catalog = LoadDefaultCultureKeys(start.Options.AdditionalFiles, defaultCulture, start.CancellationToken);

            start.RegisterSymbolAction(
                ctx => AnalyzeType(ctx, catalog, defaultCulture),
                SymbolKind.NamedType);

            // TAM004: a manual x.TenantId == … predicate is redundant now that isolation is a
            // global query filter, and a forgotten one used to be the leak. Flag every copy so the
            // DRY win can't regress; a deliberate cross-tenant read declares .IgnoreQueryFilters().
            start.RegisterOperationAction(AnalyzeTenantFilter, OperationKind.Binary);

            // TAM005: the composition rule (docs/27), found on the wire — one widened source in a
            // join strips the global filter from the whole query and silently returns other
            // tenants' rows. Method-syntax compositions only; query-syntax joins are rare here and
            // the sample's are over unscoped registry tables.
            start.RegisterSyntaxNodeAction(AnalyzeQueryComposition, SyntaxKind.InvocationExpression);

            // TAM006: the paired-atom ownership pattern (docs/28 D-AG2) is verified in BOTH
            // directions, compilation-wide. (a) A ScopedUnless/CheckOwnershipUnless call site must
            // declare its widening atom via [Widens] on the class — otherwise the atom never
            // enters the compiled catalogue and no role could ever grant it. (b) Once a widening
            // atom "R.A-all" is declared ANYWHERE, every view authorized on "R.A" must apply
            // ScopedUnless with it — the fail-closed guarantee policy-authored scopes never had.
            var ownership = new OwnershipScopeState();
            start.RegisterSymbolAction(ctx => ownership.CollectType(ctx), SymbolKind.NamedType);
            start.RegisterSyntaxNodeAction(
                ctx => ownership.CollectUnlessCall(ctx), SyntaxKind.InvocationExpression);
            start.RegisterCompilationEndAction(ctx => ownership.Report(ctx));
        });
    }

    private sealed class OwnershipScopeState
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> declaredAtoms = [];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            INamedTypeSymbol, (string Kind, string Permission, Location Location)> scoped =
            new(SymbolEqualityComparer.Default);
        private readonly System.Collections.Concurrent.ConcurrentBag<
            (string Atom, INamedTypeSymbol? Enclosing, Location Location)> unlessCalls = [];

        public void CollectType(SymbolAnalysisContext ctx)
        {
            var type = (INamedTypeSymbol)ctx.Symbol;
            foreach (var attr in type.GetAttributes())
            {
                var name = attr.AttributeClass?.ToDisplayString();
                if (name == "Tam.WidensAttribute"
                    && attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is string atom)
                    declaredAtoms.Add(atom);
            }

            // A view scopes reads (ScopedUnless); an operation scopes writes (CheckOwnershipUnless).
            // Both must apply the scope when their authorized base atom has a declared widening twin
            // — the write side is where the fail-open hole actually bites (a foreign id in the body).
            var kind = type.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "Tam.ViewAttribute") ? "View"
                : type.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "Tam.OperationAttribute") ? "Operation"
                : null;
            if (kind is null) return;
            var authorize = type.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Tam.AuthorizeAttribute");
            var permission = authorize is not null
                && authorize.ConstructorArguments.Length == 1
                && authorize.ConstructorArguments[0].Value is string p ? p : null;
            if (permission is not null)
                scoped[type] = (kind, permission, type.Locations.FirstOrDefault() ?? Location.None);
        }

        public void CollectUnlessCall(SyntaxNodeAnalysisContext ctx)
        {
            var invocation = (Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax)ctx.Node;
            if (invocation.Expression is not
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member) return;
            var methodName = member.Name.Identifier.Text;
            // The widening atom is a fixed positional argument: source.ScopedUnless(context, ATOM,
            // selector) → index 1; context.CheckOwnershipUnless(ATOM, ownerId) → index 0. Reading
            // the position (not "first string literal anywhere") avoids recording the ownerId.
            var atomIndex = methodName switch { "ScopedUnless" => 1, "CheckOwnershipUnless" => 0, _ => -1 };
            if (atomIndex < 0) return;
            var args = invocation.ArgumentList.Arguments;
            if (args.Count <= atomIndex) return;
            var atom = (args[atomIndex].Expression as
                Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax)
                ?.Token.ValueText;
            // A non-literal atom (const/nameof) can't be verified statically — record nothing so it
            // neither false-declares (a) nor false-satisfies (b); the runtime path stays fail-closed.
            if (atom is null) return;

            var enclosing = ctx.SemanticModel.GetEnclosingSymbol(
                invocation.SpanStart, ctx.CancellationToken)?.ContainingType;
            unlessCalls.Add((atom, enclosing, invocation.GetLocation()));
        }

        public void Report(CompilationAnalysisContext ctx)
        {
            var declared = new HashSet<string>(declaredAtoms, StringComparer.Ordinal);
            var appliedByType = new Dictionary<INamedTypeSymbol, HashSet<string>>(
                SymbolEqualityComparer.Default);
            foreach (var (atom, enclosing, location) in unlessCalls)
            {
                if (enclosing is null) continue;
                if (!appliedByType.TryGetValue(enclosing, out var set))
                    appliedByType[enclosing] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(atom);

                // (a) The call site's atom must be declared on the enclosing class.
                var declaredHere = enclosing.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "Tam.WidensAttribute"
                    && a.ConstructorArguments.Length == 1
                    && a.ConstructorArguments[0].Value is string declaredAtom
                    && declaredAtom == atom);
                if (!declaredHere)
                    ctx.ReportDiagnostic(Diagnostic.Create(Tam006, location,
                        $"'{enclosing.Name}' applies the widening atom '{atom}' without declaring " +
                        $"[Widens(\"{atom}\")] — the atom never enters the compiled catalogue, so no " +
                        "role could grant it and the scope would never widen."));
            }

            // (b) Every view AND operation authorized on a base whose widening atom exists must
            // apply the scope — reads via ScopedUnless, writes via CheckOwnershipUnless. Omitting
            // it on the write side is the fail-open hole a foreign id in the request body drives.
            foreach (var entry in scoped)
            {
                var type = entry.Key;
                var (kind, permission, location) = entry.Value;
                var widening = permission + "-all";
                if (!declared.Contains(widening)) continue;
                var applied = appliedByType.TryGetValue(type, out var set) && set.Contains(widening);
                if (applied) continue;
                var call = kind == "View"
                    ? $"ScopedUnless(context, \"{widening}\", …)"
                    : $"context.CheckOwnershipUnless(\"{widening}\", …)";
                ctx.ReportDiagnostic(Diagnostic.Create(Tam006, location,
                    $"{kind} '{type.Name}' is authorized on '{permission}' whose widening atom " +
                    $"'{widening}' is declared, but never applies {call} — actors without the " +
                    "widening would silently reach every row (fail-open)."));
            }
        }
    }

    private static void AnalyzeTenantFilter(OperationAnalysisContext context)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind != BinaryOperatorKind.Equals) return;

        var prop = TenantIdProperty(binary.LeftOperand) ?? TenantIdProperty(binary.RightOperand);
        if (prop is null) return;
        if (!prop.ContainingType.AllInterfaces.Any(i =>
                i.ToDisplayString() == "Tam.EntityFrameworkCore.ITenantScoped"))
            return;

        var syntax = binary.Syntax;
        // Only a query predicate (inside a lambda) — never a plain business comparison.
        if (!syntax.Ancestors().Any(a => a is Microsoft.CodeAnalysis.CSharp.Syntax.LambdaExpressionSyntax))
            return;
        // A deliberate cross-tenant read opts out with .IgnoreQueryFilters() somewhere in the chain.
        if (syntax.Ancestors()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Any(inv => inv.ToString().Contains("IgnoreQueryFilters")))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Tam004, syntax.GetLocation()));
    }

    private static IPropertySymbol? TenantIdProperty(IOperation operation) =>
        operation is IPropertyReferenceOperation { Property.Name: "TenantId" } p ? p.Property : null;

    private static readonly string[] WideningCalls = { "IgnoreQueryFilters", "InSubtree", "WithInherited" };
    private static readonly string[] ExplicitScopeCalls = { "IgnoreQueryFilters", "InSubtree", "WithInherited", "InNode", "InScope" };
    private static readonly string[] ComposingMethods = { "Join", "GroupJoin", "Concat", "Union", "Except", "Intersect" };

    private static void AnalyzeQueryComposition(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        if (!ComposingMethods.Contains(member.Name.Identifier.Text)) return;
        if (invocation.ArgumentList.Arguments.Count == 0) return;

        // The two composed sides: the receiver and the inner sequence (first argument).
        var sides = new[] { member.Expression, invocation.ArgumentList.Arguments[0].Expression };
        var resolved = sides.Select(s => ResolveChain(s, context.SemanticModel, context.CancellationToken)).ToArray();

        // Only queries that actually contain a widened source are dangerous.
        if (!resolved.Any(text => WideningCalls.Any(text.Contains))) return;

        for (var i = 0; i < sides.Length; i++)
        {
            if (ExplicitScopeCalls.Any(resolved[i].Contains)) continue;
            var type = context.SemanticModel.GetTypeInfo(sides[i], context.CancellationToken).Type;
            if (!IsTenantScopedSequence(type)) continue;
            context.ReportDiagnostic(Diagnostic.Create(Tam005, sides[i].GetLocation()));
        }
    }

    /// <summary>An identifier resolves to its local declaration's initializer, so a chain that was
    /// scoped when assigned to a variable is still recognized at the composition site.</summary>
    private static string ResolveChain(
        ExpressionSyntax expression, SemanticModel model, System.Threading.CancellationToken ct)
    {
        if (expression is IdentifierNameSyntax identifier
            && model.GetSymbolInfo(identifier, ct).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct)
                is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
            return initializer.ToString();
        return expression.ToString();
    }

    private static bool IsTenantScopedSequence(ITypeSymbol? type)
    {
        if (type is null) return false;
        foreach (var candidate in new[] { type }.Concat(type.AllInterfaces))
        {
            if (candidate is INamedTypeSymbol named
                && named.TypeArguments.Length == 1
                && (named.Name == "IQueryable" || named.Name == "IEnumerable" || named.Name == "DbSet")
                && named.TypeArguments[0].AllInterfaces.Any(i =>
                    i.ToDisplayString() == "Tam.EntityFrameworkCore.ITenantScoped"))
                return true;
        }
        return false;
    }

    private static void AnalyzeType(
        SymbolAnalysisContext context, HashSet<string>? catalog, string defaultCulture)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var operationId = AttributeArg(type, "Tam.OperationAttribute");
        var viewId = AttributeArg(type, "Tam.ViewAttribute");
        if (operationId is null && viewId is null) return;

        var id = operationId ?? viewId!;
        var location = type.Locations.FirstOrDefault() ?? Location.None;

        if (AttributeArg(type, "Tam.AuthorizeAttribute") is null)
            context.ReportDiagnostic(Diagnostic.Create(Tam001, location, id));

        if (operationId is not null)
        {
            var input = type.GetTypeMembers("Input").FirstOrDefault();
            if (input is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Tam002, location, id));
            }
            else if (catalog is not null)
            {
                CheckKey(context, catalog, defaultCulture, location,
                    $"operations.{id}.title", $"operation '{id}'");
                foreach (var (key, member) in LabelKeys(input))
                    CheckKey(context, catalog, defaultCulture, location, key, member);
            }

            var hasExecute = type.GetMembers("Execute").OfType<IMethodSymbol>()
                .Any(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public);
            if (!hasExecute)
                context.ReportDiagnostic(Diagnostic.Create(Tam003, location, id));

            // EDIT001: enums are state machines; a Change<SomeEnum> input lets callers patch
            // a state transition — the descriptive/intent line the whole design defends.
            if (input is not null)
            {
                var ctor = input.InstanceConstructors
                    .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                    .OrderByDescending(c => c.Parameters.Length)
                    .FirstOrDefault();
                foreach (var parameter in ctor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty)
                {
                    if (parameter.Type is not INamedTypeSymbol { Name: "Change", TypeArguments.Length: 1 } change)
                        continue;
                    var inner = Unwrap(change.TypeArguments[0]);
                    if (inner is { TypeKind: TypeKind.Enum })
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            Edit001, parameter.Locations.FirstOrDefault() ?? location, id, parameter.Name));
                    }
                }
            }
        }

        if (viewId is not null && catalog is not null)
        {
            var result = type.GetTypeMembers("Result").FirstOrDefault();
            if (result is not null)
            {
                foreach (var (key, member) in LabelKeys(result))
                    CheckKey(context, catalog, defaultCulture, location, key, member);
            }
        }
    }

    private static void CheckKey(
        SymbolAnalysisContext context, HashSet<string> catalog, string defaultCulture,
        Location location, string key, string referencedBy)
    {
        if (!catalog.Contains(key))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                L10n001, location, key, defaultCulture, referencedBy));
        }
    }

    /// <summary>Mirrors FieldModel label-key inference: [LabelKey] on param/property/type, else labels.{kebab}.</summary>
    private static IEnumerable<(string Key, string Member)> LabelKeys(INamedTypeSymbol record)
    {
        var ctor = record.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        if (ctor is not null && ctor.Parameters.Length > 0)
        {
            foreach (var parameter in ctor.Parameters)
            {
                var property = record.GetMembers(parameter.Name).OfType<IPropertySymbol>().FirstOrDefault();
                yield return (
                    LabelKeyOverride(parameter) ?? LabelKeyOverride(property)
                        ?? LabelKeyOverride(Unwrap(parameter.Type))
                        ?? $"labels.{Kebab(parameter.Name)}",
                    $"{record.ContainingType?.Name ?? record.Name}.{record.Name}.{parameter.Name}");
            }
            yield break;
        }

        foreach (var property in record.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && p.SetMethod is not null))
        {
            yield return (
                LabelKeyOverride(property) ?? LabelKeyOverride(Unwrap(property.Type))
                    ?? $"labels.{Kebab(property.Name)}",
                $"{record.ContainingType?.Name ?? record.Name}.{record.Name}.{property.Name}");
        }
    }

    private static ITypeSymbol? Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments.FirstOrDefault()
            : type;

    private static string? LabelKeyOverride(ISymbol? symbol) =>
        symbol?.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "Tam.LabelKeyAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static string? AttributeArg(INamedTypeSymbol type, string attributeName) =>
        type.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeName)
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    private static string Kebab(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>Flat JSON key extraction — analyzers can't ship a JSON dependency, and locale files are flat by design.</summary>
    private static HashSet<string>? LoadDefaultCultureKeys(
        ImmutableArray<AdditionalText> files, string defaultCulture, System.Threading.CancellationToken ct)
    {
        var locale = files.FirstOrDefault(f =>
            System.IO.Path.GetFileName(f.Path).Equals($"{defaultCulture}.json", StringComparison.OrdinalIgnoreCase));
        var text = locale?.GetText(ct)?.ToString();
        if (text is null) return null;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, "\"([A-Za-z0-9_.-]+)\"\\s*:"))
            keys.Add(match.Groups[1].Value);
        return keys;
    }
}
