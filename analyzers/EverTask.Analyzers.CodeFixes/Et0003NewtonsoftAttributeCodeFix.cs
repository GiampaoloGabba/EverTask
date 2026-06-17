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

/// <summary>
/// ET0003: remove the ignored Newtonsoft.Json attribute, or map the two 1:1 cases to their System.Text.Json
/// equivalents ([JsonProperty("x")] -&gt; [JsonPropertyName("x")], Newtonsoft [JsonIgnore] -&gt; STJ [JsonIgnore]).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(Et0003NewtonsoftAttributeCodeFix)), Shared]
public sealed class Et0003NewtonsoftAttributeCodeFix : CodeFixProvider
{
    private const string StjNamespace = "System.Text.Json.Serialization";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.NewtonsoftAttribute.Id);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics[0];

        var attribute = root?.FindToken(diagnostic.Location.SourceSpan.Start)
            .Parent?.AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Remove Newtonsoft.Json attribute",
                _ => Task.FromResult(RemoveAttribute(context.Document, root!, attribute)),
                equivalenceKey: DiagnosticDescriptors.NewtonsoftAttribute.Id + "-remove"),
            diagnostic);

        var mapped = MapToStj(attribute);
        if (mapped is not null)
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Replace with [{mapped.Name}]",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(
                        root!.ReplaceNode(attribute, mapped.WithTriviaFrom(attribute)
                            .WithAdditionalAnnotations(Formatter.Annotation)))),
                    equivalenceKey: DiagnosticDescriptors.NewtonsoftAttribute.Id + "-map"),
                diagnostic);
    }

    private static Document RemoveAttribute(Document document, SyntaxNode root, AttributeSyntax attribute)
    {
        var list = (AttributeListSyntax)attribute.Parent!;

        SyntaxNode newRoot;
        if (list.Attributes.Count == 1)
            newRoot = root.RemoveNode(list, SyntaxRemoveOptions.KeepNoTrivia)!;
        else
            newRoot = root.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));

        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>Returns the STJ-equivalent attribute for the two 1:1 cases, otherwise null.</summary>
    private static AttributeSyntax? MapToStj(AttributeSyntax attribute)
    {
        var simpleName = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => attribute.Name.ToString(),
        };

        var args = attribute.ArgumentList?.Arguments;

        // [JsonProperty("x")] (single string literal) -> [System.Text.Json.Serialization.JsonPropertyName("x")]
        if (simpleName is "JsonProperty" or "JsonPropertyAttribute" &&
            args is { Count: 1 } single &&
            single[0].Expression is LiteralExpressionSyntax { Token.Value: string })
            return Attribute($"{StjNamespace}.JsonPropertyName").WithArgumentList(attribute.ArgumentList);

        // Newtonsoft [JsonIgnore] (no args) -> STJ [JsonIgnore]
        if (simpleName is "JsonIgnore" or "JsonIgnoreAttribute" && (args is null || args.Value.Count == 0))
            return Attribute($"{StjNamespace}.JsonIgnore");

        return null;
    }

    private static AttributeSyntax Attribute(string fullyQualifiedName) =>
        SyntaxFactory.Attribute(SyntaxFactory.ParseName(fullyQualifiedName));
}
