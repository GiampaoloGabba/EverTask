using System.Text.Json;

namespace EverTask.Tests.Serialization;

// Promoted from the (deleted) EverTask.Tests.SerializationPoc project so the breadth suite runs in CI
// against the REAL EverTaskJson serializer. Shared payload types used by the promoted round-trip tests.

public enum PocPriority { Low, Normal, High }

/// <summary>
/// "Complex but well-behaved" payload: primitives, Guid, DateTimeOffset, TimeSpan, decimal, enum,
/// nullable, collections, dictionary, and a nested record with a parameterized ctor + init property.
/// </summary>
public record ComplexTask(
    Guid OrderId,
    int Count,
    long BigNumber,
    decimal Amount,
    double Ratio,
    bool Enabled,
    DateTimeOffset When,
    TimeSpan Ttl,
    PocPriority Priority,
    string? Note,
    List<int> Ids,
    string[] Tags,
    Dictionary<string, string> Metadata,
    NestedDto Nested) : IEverTask;

public record NestedDto(string Name, int Value)
{
    public bool Flag { get; init; }
}

/// <summary>
/// KNOWN STJ gap: a property whose only setter is non-public AND has no matching ctor parameter is silently
/// dropped on read. Documents the payload contract for task authors after the migration.
/// </summary>
public class PrivateSetterTask : IEverTask
{
    public PrivateSetterTask() { }

    public PrivateSetterTask(string visible, string hidden)
    {
        Visible = visible;
        Hidden  = hidden;
    }

    public string Visible { get; set; } = "";
    public string Hidden  { get; private set; } = "";
}

/// <summary>
/// <c>Dictionary&lt;string, object&gt;</c>: under STJ the values come back as <see cref="JsonElement"/>
/// (Newtonsoft returned boxed primitives / JObject).
/// </summary>
public class ObjectBagTask : IEverTask
{
    public Dictionary<string, object> Bag { get; set; } = new();
}

/// <summary>Plain concrete task — proves serializing through an <c>IEverTask</c>/<c>object</c> root captures
/// the CONCRETE runtime type's properties (the load-bearing behavior EverTask relies on).</summary>
public record MarkerTask(int X, string Y) : IEverTask;

public abstract class Shape
{
    public string Kind { get; set; } = "";
}

public class Circle : Shape
{
    public double Radius { get; set; }
}

/// <summary>A payload with a NESTED property typed as an abstract base — the only real "polymorphism"
/// surface. Neither the Newtonsoft config (TypeNameHandling.None) nor STJ round-trips the derived members;
/// a documented contract limit, not a regression.</summary>
public record TaskWithBaseProperty(Shape Shape) : IEverTask;
