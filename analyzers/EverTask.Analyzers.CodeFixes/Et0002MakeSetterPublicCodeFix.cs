using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace EverTask.Analyzers;

/// <summary>ET0002: give the property a public setter (add one, or strip the non-public modifier).</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(Et0002MakeSetterPublicCodeFix)), Shared]
public sealed class Et0002MakeSetterPublicCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.DroppedSetter.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];

        var property = root?.FindToken(diagnostic.Location.SourceSpan.Start)
            .Parent?.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
        if (property?.AccessorList is null)
            return; // expression-bodied get-only: nothing safe to auto-fix.

        context.RegisterCodeFix(
            CodeAction.Create(
                "Add a public setter",
                _ => Task.FromResult(MakeSetterPublic(context.Document, root!, property)),
                equivalenceKey: DiagnosticDescriptors.DroppedSetter.Id),
            diagnostic);
    }

    private static Document MakeSetterPublic(Document document, SyntaxNode root, PropertyDeclarationSyntax property)
    {
        var accessors = property.AccessorList!;
        var existingSetter = accessors.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));

        AccessorListSyntax newAccessors;
        if (existingSetter is not null)
        {
            // Strip the non-public modifier (private/protected/internal) -> defaults to the property's accessibility.
            var publicSetter = existingSetter.WithModifiers(SyntaxFactory.TokenList());
            newAccessors = accessors.ReplaceNode(existingSetter, publicSetter);
        }
        else
        {
            var setter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            newAccessors = accessors.AddAccessors(setter);
        }

        var newRoot = root.ReplaceNode(property, property.WithAccessorList(newAccessors)
            .WithAdditionalAnnotations(Formatter.Annotation));
        return document.WithSyntaxRoot(newRoot);
    }
}
