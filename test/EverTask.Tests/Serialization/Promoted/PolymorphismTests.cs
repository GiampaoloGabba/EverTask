using Newtonsoft.Json;

using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Pins the exact polymorphism story, because "we don't use polymorphism" is more nuanced than yes/no:
/// <list type="number">
/// <item>EverTask's task-TYPE dispatch IS polymorphic, but resolved via the stored assembly-qualified type
/// name (not a JSON <c>$type</c>) — so it works under STJ with no special config.</item>
/// <item>Serializing through an <c>IEverTask</c>/<c>object</c> ROOT must capture the CONCRETE type's
/// properties (load-bearing: must NOT be "optimized" to a generic <c>Serialize&lt;IEverTask&gt;</c>, which
/// would emit <c>{}</c>).</item>
/// <item>A NESTED property typed as an abstract base / interface is the only true polymorphism surface, and
/// it is unsupported by BOTH the current Newtonsoft config and STJ — a documented contract limit, no
/// regression.</item>
/// </list>
/// </summary>
public class PolymorphismTests
{
    private static readonly JsonSerializerSettings Legacy =
        new() { TypeNameHandling = TypeNameHandling.None };

    [Fact]
    public void Serializing_through_IEverTask_root_captures_concrete_type()
    {
        IEverTask task = new MarkerTask(7, "hi"); // static type IEverTask (marker, zero properties)

        var json = EverTaskJson.Serialize(task);    // facade param is object? → STJ uses the RUNTIME type

        json.ShouldNotBe("{}");
        json.ShouldContain("\"X\"", Case.Sensitive);
        json.ShouldContain("\"Y\"", Case.Sensitive);

        // Recovery resolves the concrete type by name and deserializes into it.
        var restored = (MarkerTask)EverTaskJson.Deserialize(json, typeof(MarkerTask))!;
        restored.X.ShouldBe(7);
        restored.Y.ShouldBe("hi");
    }

    [Fact]
    public void No_dollar_type_marker_is_emitted()
    {
        // Parity with EverTaskJson's TypeNameHandling.None: no gadget-deserialization surface.
        var json = EverTaskJson.Serialize(new MarkerTask(1, "a"));
        json.ShouldNotContain("$type");
    }

    [Fact]
    public void Nested_abstract_property_drops_derived_members_on_serialize()
    {
        var task = new TaskWithBaseProperty(new Circle { Kind = "circle", Radius = 5 });

        var json = EverTaskJson.Serialize(task);

        // STJ serializes the nested property by its DECLARED type (Shape): the Circle-only Radius is omitted.
        json.ShouldContain("Kind", Case.Sensitive);
        json.ShouldNotContain("Radius");
    }

    [Fact]
    public void Nested_abstract_property_cannot_be_deserialized_same_limit_as_Newtonsoft()
    {
        var json = EverTaskJson.Serialize(new TaskWithBaseProperty(new Circle { Kind = "c", Radius = 1 }));

        // STJ cannot instantiate the abstract Shape → throws. Newtonsoft (TypeNameHandling.None) cannot
        // either. Same contract limit before and after the migration → no regression.
        Should.Throw<Exception>(() => EverTaskJson.Deserialize(json, typeof(TaskWithBaseProperty)));

        var legacyJson = JsonConvert.SerializeObject(new TaskWithBaseProperty(new Circle { Kind = "c", Radius = 1 }), Legacy);
        Should.Throw<Exception>(() => JsonConvert.DeserializeObject<TaskWithBaseProperty>(legacyJson, Legacy));
    }
}
