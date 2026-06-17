using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EverTask.Analyzers;

/// <summary>
/// Validates the System.Text.Json payload contract for every <c>IEverTask</c> type and the transitive
/// closure of types reachable through their serialized members. Moves contract violations that today only
/// surface at runtime (on recovery) left to the IDE / build. See issue #14.
/// </summary>
/// <remarks>
/// The payload closure is computed once in the compilation-start action; per-member diagnostics are then
/// reported from a per-symbol action so they stay <i>local</i> (a prerequisite for the code fixes to apply).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PayloadContractAnalyzer : DiagnosticAnalyzer
{
    private const int MaxDepth = 16;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        DiagnosticDescriptors.PublicField,
        DiagnosticDescriptors.DroppedSetter,
        DiagnosticDescriptors.NewtonsoftAttribute,
        DiagnosticDescriptors.UndeclaredPolymorphism,
        DiagnosticDescriptors.JsonElementProperty,
        DiagnosticDescriptors.NonSerializableType,
        DiagnosticDescriptors.UnresolvableConstructor);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var symbols = KnownSymbols.TryCreate(context.Compilation);
        if (symbols is null)
            return; // compilation does not reference EverTask.Abstractions -> nothing to validate.

        var payloadTypes = BuildPayloadClosure(context.Compilation, symbols);
        if (payloadTypes.Count == 0)
            return;

        context.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (payloadTypes.Contains(type))
                AnalyzeType(symbolContext, symbols, type);
        }, SymbolKind.NamedType);
    }

    /// <summary>
    /// Set of source types to validate: every <c>IEverTask</c> implementor plus the in-source types reachable
    /// through their serialized members (visited-set cycle guard + depth bound).
    /// </summary>
    private static HashSet<INamedTypeSymbol> BuildPayloadClosure(Compilation compilation, KnownSymbols symbols)
    {
        var closure = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<(INamedTypeSymbol Type, int Depth)>();

        foreach (var type in EnumerateSourceTypes(compilation.Assembly.GlobalNamespace))
            if (type.TypeKind is TypeKind.Class or TypeKind.Struct && symbols.ImplementsIEverTask(type) && closure.Add(type))
                queue.Enqueue((type, 0));

        while (queue.Count > 0)
        {
            var (type, depth) = queue.Dequeue();
            if (depth >= MaxDepth)
                continue;

            foreach (var next in ReachableTypes(type, symbols))
                if (IsInSource(next) && closure.Add(next))
                    queue.Enqueue((next, depth + 1));
        }

        return closure;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateSourceTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                foreach (var nested in EnumerateSourceTypes(childNs))
                    yield return nested;
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in EnumerateNestedTypes(type))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNestedTypes(nested))
                yield return deeper;
        }
    }

    private static void AnalyzeType(SymbolAnalysisContext context, KnownSymbols symbols, INamedTypeSymbol type)
    {
        ReportNewtonsoftAttributes(context, type); // type-level attributes ([JsonObject], ...)

        if (symbols.HasUnresolvableConstructor(type))
            Report(context, DiagnosticDescriptors.UnresolvableConstructor, type, type.Name);

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Constructor } ctor:
                    ReportNewtonsoftAttributes(context, ctor); // Newtonsoft [JsonConstructor]
                    break;

                case IFieldSymbol field when symbols.IsUnserializedPublicField(field):
                    ReportNewtonsoftAttributes(context, field);
                    Report(context, DiagnosticDescriptors.PublicField, field, field.Name);
                    break;

                case IPropertySymbol property when symbols.IsSerializedProperty(property):
                    AnalyzeSerializedProperty(context, symbols, type, property);
                    break;
            }
        }
    }

    private static void AnalyzeSerializedProperty(
        SymbolAnalysisContext context, KnownSymbols symbols, INamedTypeSymbol declaringType, IPropertySymbol property)
    {
        ReportNewtonsoftAttributes(context, property);

        if (symbols.IsDroppedOnRead(property, declaringType))
            Report(context, DiagnosticDescriptors.DroppedSetter, property, property.Name);

        var elementType = TypeHelpers.Unwrap(property.Type);

        if ((elementType.TypeKind == TypeKind.Interface ||
             elementType is { IsAbstract: true, TypeKind: TypeKind.Class }) &&
            !symbols.HasDeclaredPolymorphism(elementType))
            Report(context, DiagnosticDescriptors.UndeclaredPolymorphism, property, property.Name, elementType.Name);

        if (TypeHelpers.DeserializesToJsonElement(property.Type))
            Report(context, DiagnosticDescriptors.JsonElementProperty, property, property.Name);

        if (symbols.IsNonSerializableType(elementType))
            Report(context, DiagnosticDescriptors.NonSerializableType, property, property.Name, elementType.Name);
    }

    /// <summary>Types reachable through serialized members + base type, for the closure walk.</summary>
    private static IEnumerable<INamedTypeSymbol> ReachableTypes(INamedTypeSymbol type, KnownSymbols symbols)
    {
        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            yield return baseType;

        foreach (var member in type.GetMembers())
        {
            ITypeSymbol? memberType = member switch
            {
                IPropertySymbol p when symbols.IsSerializedProperty(p) => p.Type,
                IFieldSymbol f when symbols.HasAttribute(f, symbols.JsonIncludeAttribute) => f.Type,
                _ => null,
            };

            if (memberType is not null && TypeHelpers.Unwrap(memberType) is INamedTypeSymbol named)
                yield return named;
        }
    }

    private static void ReportNewtonsoftAttributes(SymbolAnalysisContext context, ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (!KnownSymbols.IsNewtonsoftAttribute(attr))
                continue;

            var location = attr.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();
            if (location is null)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NewtonsoftAttribute, location, attr.AttributeClass!.Name));
        }
    }

    private static void Report(
        SymbolAnalysisContext context, DiagnosticDescriptor descriptor, ISymbol symbol, params object[] messageArgs)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
    }

    private static bool IsInSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);
}
