using System.Linq;
using Microsoft.CodeAnalysis;

namespace EverTask.Analyzers;

/// <summary>TFM-agnostic type inspection shared by the payload-contract rules.</summary>
internal static class TypeHelpers
{
    /// <summary>
    /// Unwraps <c>Nullable&lt;T&gt;</c>, arrays and single-element generic collections to the element type
    /// the contract actually round-trips (used by ET0004 / ET0006). Dictionaries (two type args) are left
    /// intact. Strings are never unwrapped to <c>char</c>.
    /// </summary>
    public static ITypeSymbol Unwrap(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return Unwrap(array.ElementType);

        if (type is INamedTypeSymbol named)
        {
            if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return Unwrap(named.TypeArguments[0]);

            if (type.SpecialType == SpecialType.System_String)
                return type;

            if (named.TypeArguments.Length == 1)
            {
                var element = named.TypeArguments[0];
                var isEnumerableOfElement = named.AllInterfaces
                    .Concat(new[] { named })
                    .Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
                              SymbolEqualityComparer.Default.Equals(i.TypeArguments.FirstOrDefault(), element));
                if (isEnumerableOfElement)
                    return Unwrap(element);
            }
        }

        return type;
    }

    /// <summary>
    /// object / dynamic / <c>(I)Dictionary&lt;string, object&gt;</c> all deserialize to JsonElement (ET0005).
    /// </summary>
    public static bool DeserializesToJsonElement(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Dynamic || type.SpecialType == SpecialType.System_Object)
            return true;

        if (type is not INamedTypeSymbol named)
            return false;

        var candidates = named.AllInterfaces.Concat(new[] { named });
        foreach (var candidate in candidates)
        {
            if (candidate.TypeArguments.Length == 2 &&
                candidate.Name.Contains("Dictionary") &&
                candidate.TypeArguments[1].SpecialType == SpecialType.System_Object)
                return true;
        }

        return false;
    }
}
