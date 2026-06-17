using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

namespace EverTask.Analyzers;

/// <summary>
/// ET0004: scaffold declarative polymorphism on the abstract/interface base type — adds
/// [JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")] plus one [JsonDerivedType(typeof(T), "alias")]
/// per derived type discovered in the solution. Offered only when at least one derived type exists.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(Et0004ScaffoldPolymorphismCodeFix)), Shared]
public sealed class Et0004ScaffoldPolymorphismCodeFix : CodeFixProvider
{
    private const string StjNamespace = "System.Text.Json.Serialization";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.UndeclaredPolymorphism.Id);

    // Cross-document solution edits: no batch fixer (avoids conflicting edits on the same base type).
    public override FixAllProvider? GetFixAllProvider() => null;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];

        var property = root?.FindToken(diagnostic.Location.SourceSpan.Start)
            .Parent?.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
        if (property is null || model is null)
            return;

        if (model.GetDeclaredSymbol(property, context.CancellationToken) is not IPropertySymbol propertySymbol)
            return;

        var baseType = TypeHelpers.Unwrap(propertySymbol.Type) as INamedTypeSymbol;
        if (baseType is null || !baseType.DeclaringSyntaxReferences.Any())
            return; // base type is not in source -> cannot scaffold.

        context.RegisterCodeFix(
            CodeAction.Create(
                $"Declare polymorphism on '{baseType.Name}'",
                ct => ScaffoldAsync(context.Document.Project.Solution, baseType, ct),
                equivalenceKey: DiagnosticDescriptors.UndeclaredPolymorphism.Id),
            diagnostic);
    }

    private static async Task<Solution> ScaffoldAsync(Solution solution, INamedTypeSymbol baseType, CancellationToken ct)
    {
        var derived = baseType.TypeKind == TypeKind.Interface
            ? await SymbolFinder.FindImplementationsAsync(baseType, solution, cancellationToken: ct).ConfigureAwait(false)
            : await SymbolFinder.FindDerivedClassesAsync(baseType, solution, cancellationToken: ct).ConfigureAwait(false);

        var concrete = derived.OfType<INamedTypeSymbol>()
            .Where(t => t is { IsAbstract: false, TypeKind: TypeKind.Class })
            .ToList();
        if (concrete.Count == 0)
            return solution; // nothing to scaffold; the diagnostic message guides the manual fix.

        var reference = baseType.DeclaringSyntaxReferences[0];
        if (reference.GetSyntax(ct) is not TypeDeclarationSyntax declaration)
            return solution;

        var document = solution.GetDocument(reference.SyntaxTree);
        if (document is null)
            return solution;

        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null)
            return solution;

        var newDeclaration = declaration.WithAttributeLists(declaration.AttributeLists
            .Add(PolymorphicAttribute())
            .AddRange(concrete.Select(DerivedTypeAttribute)))
            .WithAdditionalAnnotations(Formatter.Annotation);

        return document.WithSyntaxRoot(root.ReplaceNode(declaration, newDeclaration)).Project.Solution;
    }

    private static AttributeListSyntax PolymorphicAttribute()
    {
        var arg = SyntaxFactory.AttributeArgument(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("$kind")))
            .WithNameEquals(SyntaxFactory.NameEquals("TypeDiscriminatorPropertyName"));

        return AttributeList(SyntaxFactory.Attribute(SyntaxFactory.ParseName($"{StjNamespace}.JsonPolymorphic"))
            .WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(arg))));
    }

    private static AttributeListSyntax DerivedTypeAttribute(INamedTypeSymbol derived)
    {
        var fqn = derived.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var alias = ToAlias(derived.Name);

        var args = SyntaxFactory.SeparatedList(new[]
        {
            SyntaxFactory.AttributeArgument(SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(fqn))),
            SyntaxFactory.AttributeArgument(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(alias))),
        });

        return AttributeList(SyntaxFactory.Attribute(SyntaxFactory.ParseName($"{StjNamespace}.JsonDerivedType"))
            .WithArgumentList(SyntaxFactory.AttributeArgumentList(args)));
    }

    private static AttributeListSyntax AttributeList(AttributeSyntax attribute) =>
        SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attribute));

    private static string ToAlias(string typeName)
    {
        var name = typeName.Length > 0 ? char.ToLowerInvariant(typeName[0]) + typeName.Substring(1) : typeName;
        return name;
    }
}
