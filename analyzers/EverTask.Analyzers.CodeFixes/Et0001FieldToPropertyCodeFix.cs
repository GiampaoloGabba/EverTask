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

/// <summary>ET0001: convert a non-serialized public field into a public auto-property.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(Et0001FieldToPropertyCodeFix)), Shared]
public sealed class Et0001FieldToPropertyCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.PublicField.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];

        var field = root?.FindToken(diagnostic.Location.SourceSpan.Start)
            .Parent?.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault();
        if (field is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Convert field to property",
                _ => Task.FromResult(ConvertToProperty(context.Document, root!, field)),
                equivalenceKey: DiagnosticDescriptors.PublicField.Id),
            diagnostic);
    }

    private static Document ConvertToProperty(Document document, SyntaxNode root, FieldDeclarationSyntax field)
    {
        var modifiers = field.Modifiers;
        var type = field.Declaration.Type;

        var properties = field.Declaration.Variables.Select(variable =>
        {
            var declaration = SyntaxFactory.PropertyDeclaration(type.WithoutTrailingTrivia(), variable.Identifier.WithoutTrivia())
                .WithModifiers(modifiers)
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[]
                {
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                })));

            if (variable.Initializer is not null)
                declaration = declaration
                    .WithInitializer(variable.Initializer)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

            return (MemberDeclarationSyntax)declaration;
        }).ToArray();

        // Preserve leading trivia (doc comments) on the first generated property.
        properties[0] = properties[0].WithLeadingTrivia(field.GetLeadingTrivia());
        properties[properties.Length - 1] = properties[properties.Length - 1].WithTrailingTrivia(field.GetTrailingTrivia());

        var newRoot = root.ReplaceNode(field, properties).WithAdditionalAnnotations(Formatter.Annotation);
        return document.WithSyntaxRoot(newRoot);
    }
}
