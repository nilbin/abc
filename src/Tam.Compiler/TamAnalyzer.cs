using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

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

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Tam001, Tam002, Tam003, L10n001, Edit001];

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
        });
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
