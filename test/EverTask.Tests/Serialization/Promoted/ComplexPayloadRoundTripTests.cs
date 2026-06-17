using System.Text.Json;

using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Round-trip of "complex" user payloads through the candidate STJ serializer, plus the two documented
/// STJ limitations (non-public setter without ctor param; <c>object</c> values). These define the contract
/// task authors must follow after the migration.
/// </summary>
public class ComplexPayloadRoundTripTests
{
    private static ComplexTask Sample() => new(
        OrderId:   Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Count:     42,
        BigNumber: 9_000_000_000L,
        Amount:    1234.56m,
        Ratio:     0.3333333333333333,
        Enabled:   true,
        When:      new DateTimeOffset(2026, 6, 17, 14, 30, 15, TimeSpan.FromHours(2)),
        Ttl:       TimeSpan.FromHours(25).Add(TimeSpan.FromSeconds(5)),
        Priority:  PocPriority.High,
        Note:      null,
        Ids:       new List<int> { 1, 2, 3 },
        Tags:      new[] { "a", "b" },
        Metadata:  new Dictionary<string, string> { ["k1"] = "v1", ["k2"] = "v2" },
        Nested:    new NestedDto("inner", 7) { Flag = true });

    private static void AssertEqual(ComplexTask expected, ComplexTask actual)
    {
        actual.OrderId.ShouldBe(expected.OrderId);
        actual.Count.ShouldBe(expected.Count);
        actual.BigNumber.ShouldBe(expected.BigNumber);
        actual.Amount.ShouldBe(expected.Amount);
        actual.Ratio.ShouldBe(expected.Ratio);
        actual.Enabled.ShouldBe(expected.Enabled);
        actual.When.ShouldBe(expected.When);
        actual.Ttl.ShouldBe(expected.Ttl);
        actual.Priority.ShouldBe(expected.Priority);
        actual.Note.ShouldBe(expected.Note);
        actual.Ids.ShouldBe(expected.Ids);
        actual.Tags.ShouldBe(expected.Tags);
        actual.Metadata.ShouldBe(expected.Metadata);
        actual.Nested.Name.ShouldBe(expected.Nested.Name);
        actual.Nested.Value.ShouldBe(expected.Nested.Value);
        actual.Nested.Flag.ShouldBe(expected.Nested.Flag);
    }

    [Fact]
    public void ComplexTask_roundtrips_through_STJ()
    {
        var original = Sample();
        var restored = EverTaskJson.Deserialize<ComplexTask>(EverTaskJson.Serialize(original))!;
        AssertEqual(original, restored);
    }

    [Fact]
    public void STJ_keeps_PascalCase_keys_no_camelCase()
    {
        var json = EverTaskJson.Serialize(Sample());
        // Case.Sensitive: Shouldly's default string compare is case-insensitive, which would defeat the point.
        json.ShouldContain("\"OrderId\"", Case.Sensitive);
        json.ShouldNotContain("\"orderId\"", Case.Sensitive);
    }

    [Fact]
    public void PrivateSetter_without_ctor_param_is_LOST_under_STJ()
    {
        var original = new PrivateSetterTask("shown", "secret");

        var restored = EverTaskJson.Deserialize<PrivateSetterTask>(EverTaskJson.Serialize(original))!;

        restored.Visible.ShouldBe("shown");
        // Documented limitation: STJ ignores the private setter on read.
        restored.Hidden.ShouldBe("");
    }

    [Fact]
    public void ObjectBag_values_come_back_as_JsonElement()
    {
        var original = new ObjectBagTask
        {
            Bag = new Dictionary<string, object> { ["n"] = 5, ["s"] = "x", ["b"] = true }
        };

        var restored = EverTaskJson.Deserialize<ObjectBagTask>(EverTaskJson.Serialize(original))!;

        // Documented behavior difference vs Newtonsoft (which gave boxed primitives / JObject).
        restored.Bag["n"].ShouldBeOfType<JsonElement>();
        ((JsonElement)restored.Bag["n"]).GetInt32().ShouldBe(5);
        ((JsonElement)restored.Bag["s"]).GetString().ShouldBe("x");
    }
}
