using System.Linq;
using Microsoft.CodeAnalysis;

namespace EverTask.Analyzers;

/// <summary>
/// Compilation-scoped symbol cache + the predicates that mirror <c>EverTaskJson.Options</c> (PascalCase,
/// properties only, IncludeFields off, STJ attributes honored, Newtonsoft attributes ignored).
/// </summary>
internal sealed class KnownSymbols
{
    public const string IEverTaskMetadataName = "EverTask.Abstractions.IEverTask";

    private KnownSymbols(INamedTypeSymbol everTask) => IEverTask = everTask;

    public INamedTypeSymbol IEverTask { get; }

    // System.Text.Json attributes (honored by the contract).
    public INamedTypeSymbol? JsonIgnoreAttribute { get; private set; }
    public INamedTypeSymbol? JsonIncludeAttribute { get; private set; }
    public INamedTypeSymbol? JsonPolymorphicAttribute { get; private set; }
    public INamedTypeSymbol? JsonDerivedTypeAttribute { get; private set; }
    public INamedTypeSymbol? JsonConstructorAttribute { get; private set; }

    // ET0006 well-known non-serializable types (may be null when not referenced).
    public INamedTypeSymbol? Stream { get; private set; }
    public INamedTypeSymbol? SystemType { get; private set; }
    public INamedTypeSymbol? CancellationToken { get; private set; }
    public INamedTypeSymbol? DbContext { get; private set; }

    public static KnownSymbols? TryCreate(Compilation compilation)
    {
        var everTask = compilation.GetTypeByMetadataName(IEverTaskMetadataName);
        if (everTask is null)
            return null;

        return new KnownSymbols(everTask)
        {
            JsonIgnoreAttribute      = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute"),
            JsonIncludeAttribute     = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIncludeAttribute"),
            JsonPolymorphicAttribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonPolymorphicAttribute"),
            JsonDerivedTypeAttribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonDerivedTypeAttribute"),
            JsonConstructorAttribute = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonConstructorAttribute"),
            Stream                   = compilation.GetTypeByMetadataName("System.IO.Stream"),
            SystemType               = compilation.GetTypeByMetadataName("System.Type"),
            CancellationToken        = compilation.GetTypeByMetadataName("System.Threading.CancellationToken"),
            DbContext                = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext"),
        };
    }

    public bool ImplementsIEverTask(INamedTypeSymbol type) =>
        type.AllInterfaces.Contains(IEverTask, SymbolEqualityComparer.Default);

    public bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
    {
        if (attributeType is null)
            return false;

        foreach (var attr in symbol.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                return true;

        return false;
    }

    /// <summary>Newtonsoft.Json attributes are matched by namespace so we don't enumerate every type.</summary>
    public static bool IsNewtonsoftAttribute(AttributeData attr) =>
        attr.AttributeClass is { } cls &&
        cls.ContainingNamespace.ToDisplayString().StartsWith("Newtonsoft.Json", System.StringComparison.Ordinal);

    /// <summary>
    /// A property STJ writes: public, instance, readable getter (or opted in with [JsonInclude]) and not
    /// [JsonIgnore]. Record positional parameters surface as public init-only properties and are covered here.
    /// </summary>
    public bool IsSerializedProperty(IPropertySymbol property)
    {
        if (property.IsStatic || property.IsIndexer || property.IsImplicitlyDeclared && property.Name == "EqualityContract")
            return false;

        if (HasAttribute(property, JsonIgnoreAttribute))
            return false;

        var hasPublicGetter = property.GetMethod is { DeclaredAccessibility: Accessibility.Public };
        return hasPublicGetter || HasAttribute(property, JsonIncludeAttribute);
    }

    /// <summary>
    /// A public instance field that STJ does NOT persist (IncludeFields off) unless [JsonInclude] is present.
    /// </summary>
    public bool IsUnserializedPublicField(IFieldSymbol field) =>
        field is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsConst: false, IsImplicitlyDeclared: false } &&
        !HasAttribute(field, JsonIncludeAttribute);

    /// <summary>
    /// True when STJ cannot populate <paramref name="property"/> on read: no usable setter (missing, or
    /// non-public and not init) AND no [JsonInclude] AND no constructor parameter matching by name.
    /// </summary>
    public bool IsDroppedOnRead(IPropertySymbol property, INamedTypeSymbol declaringType)
    {
        if (HasAttribute(property, JsonIncludeAttribute))
            return false;

        var setter = property.SetMethod;
        var hasUsableSetter = setter is not null &&
                              (setter.IsInitOnly || setter.DeclaredAccessibility == Accessibility.Public);
        if (hasUsableSetter)
            return false;

        return !HasMatchingConstructorParameter(declaringType, property.Name);
    }

    private static bool HasMatchingConstructorParameter(INamedTypeSymbol type, string propertyName)
    {
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility == Accessibility.Private)
                continue;

            foreach (var param in ctor.Parameters)
                if (string.Equals(param.Name, propertyName, System.StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        return false;
    }

    public bool HasDeclaredPolymorphism(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            var hasPolymorphic = false;
            var hasDerived = false;
            foreach (var attr in current.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, JsonPolymorphicAttribute))
                    hasPolymorphic = true;
                else if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, JsonDerivedTypeAttribute))
                    hasDerived = true;
            }

            if (hasPolymorphic && hasDerived)
                return true;
        }

        return false;
    }

    /// <summary>
    /// STJ cannot pick a constructor when a type exposes more than one public constructor and none is
    /// parameterless or marked [JsonConstructor] — deserialization throws on recovery (ET0007). Structs (always
    /// have an implicit parameterless ctor) and records (single public primary ctor; the copy ctor is protected)
    /// never trip this.
    /// </summary>
    public bool HasUnresolvableConstructor(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic)
            return false;

        var publicCtors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .ToArray();

        if (publicCtors.Length < 2)
            return false;

        if (publicCtors.Any(c => c.Parameters.Length == 0))
            return false;

        return !publicCtors.Any(c => HasAttribute(c, JsonConstructorAttribute));
    }

    public bool IsNonSerializableType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsTupleType: true })
            return true; // ValueTuple exposes Item1.. as fields -> dropped by STJ.

        if (type.TypeKind == TypeKind.Delegate)
            return true;

        switch (type.SpecialType)
        {
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                return true;
        }

        if (SymbolEqualityComparer.Default.Equals(type, SystemType) ||
            SymbolEqualityComparer.Default.Equals(type, CancellationToken))
            return true;

        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, Stream) ||
                SymbolEqualityComparer.Default.Equals(current, DbContext))
                return true;

        return false;
    }
}
