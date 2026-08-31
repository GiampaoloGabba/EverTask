using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EverTask.Analyzers;

/// <summary>
/// Compile-time mirror of the <c>HandlerRegistrar</c> startup warnings, so problems surface in the
/// editor instead of in the host log:
/// ET0011 — a concrete open-generic type implementing <c>IEverTaskHandler&lt;T&gt;</c> is skipped by
/// the assembly scan and never registered (runtime G1);
/// ET0012 — two or more concrete handlers for the same closed task contract in one compilation;
/// the scan registers only the first one discovered (runtime G2). Duplicates split across
/// assemblies are out of reach here and remain a startup warning.
/// Grouping is by EXACT closed interface identity, like the runtime scanner: the interface is
/// contravariant, and assignability would wrongly pair a base-task handler with a derived task.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerRegistrationAnalyzer : DiagnosticAnalyzer
{
    private const string HandlerInterfaceMetadataName = "EverTask.Abstractions.IEverTaskHandler`1";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            DiagnosticDescriptors.OpenGenericHandler,
            DiagnosticDescriptors.DuplicateHandler);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        // Generated handlers are scanned at runtime like any other type, so they must count here
        // too: a generated duplicate silently defeating ET0012 would contradict the G2 warning.
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var handlerInterface = context.Compilation.GetTypeByMetadataName(HandlerInterfaceMetadataName);
        if (handlerInterface is null)
            return;

        // Closed contract -> concrete handlers declared in this compilation (thread-safe: symbol
        // actions run concurrently). Reported once, deterministically ordered, at compilation end.
        var handlersByContract =
            new ConcurrentDictionary<INamedTypeSymbol, ConcurrentBag<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

        context.RegisterSymbolAction(
            symbolContext => CollectHandler(symbolContext, handlerInterface, handlersByContract),
            SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext => ReportDuplicates(endContext, handlersByContract));
    }

    private static void CollectHandler(
        SymbolAnalysisContext context,
        INamedTypeSymbol handlerInterface,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentBag<INamedTypeSymbol>> handlersByContract)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;

        // Mirrors the runtime eligibility check: concrete classes/structs only (a static class is
        // abstract+sealed and drops out here, like the scan's IsAbstract filter).
        if (symbol.IsAbstract || symbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return;

        var implementsHandler = false;
        // Kept as a plain loop: a LINQ form would allocate an enumerator per analyzed type.
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var iface in symbol.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, handlerInterface))
                continue;

            implementsHandler = true;

            if (!IsOpen(symbol))
                handlersByContract.GetOrAdd(iface, static _ => []).Add(symbol);
        }

        if (implementsHandler && IsOpen(symbol))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.OpenGenericHandler,
                symbol.Locations[0],
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    /// <summary>Open type parameters anywhere in the nesting chain, like ContainsGenericParameters.</summary>
    private static bool IsOpen(INamedTypeSymbol symbol)
    {
        for (var type = symbol; type is not null; type = type.ContainingType)
        {
            if (type.TypeParameters.Length > 0)
                return true;
        }

        return false;
    }

    private static void ReportDuplicates(
        CompilationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentBag<INamedTypeSymbol>> handlersByContract)
    {
        // Stable contract order: the ConcurrentDictionary's enumeration order follows its internal
        // bucket layout, which would make multi-contract reports non-deterministic.
        foreach (var contract in handlersByContract.OrderBy(
                     static c => c.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            if (contract.Value.Count < 2)
                continue;

            // Deterministic order for stable messages (symbol actions collect concurrently).
            var handlers = contract.Value
                           .OrderBy(h => h.ToDisplayString(), StringComparer.Ordinal)
                           .ToArray();

            // Fully qualified identities: two same-named handlers in different namespaces (or
            // nesting) would otherwise read as the same type in the message.
            var taskName = contract.Key.TypeArguments[0]
                                   .ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

            // One squiggle per implicated handler: each declaration is part of the ambiguity.
            foreach (var handler in handlers)
            {
                var others = string.Join(", ", handlers
                    .Where(h => !SymbolEqualityComparer.Default.Equals(h, handler))
                    .Select(h => $"'{h.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}'"));

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateHandler,
                    handler.Locations[0],
                    taskName,
                    handler.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    others));
            }
        }
    }
}
