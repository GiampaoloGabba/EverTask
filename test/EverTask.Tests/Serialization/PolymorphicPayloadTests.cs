using EverTask.Serialization;

namespace EverTask.Tests.Serialization;

/// <summary>
/// First-class support for a NESTED polymorphic payload property via STJ's DECLARATIVE polymorphism
/// (<c>[JsonPolymorphic]</c> + <c>[JsonDerivedType]</c>). This is the supported escape hatch from the
/// "nested abstract/interface property is not round-tripped" limit: the discriminator is a CLOSED, declared
/// set of types — NOT arbitrary type loading — so the concrete subtype + its members round-trip while the
/// L33 gadget-deserialization isolation invariant is preserved (no `Type.GetType(arbitrary-string)`).
/// </summary>
public class PolymorphicPayloadTests
{
    [Fact]
    public void Declarative_polymorphic_nested_property_round_trips_concrete_subtype()
    {
        IEverTask task = new PolymorphicNotifyTask(new EmailChannel { Address = "a@b.it" });

        // Root is object? → STJ serializes the concrete PolymorphicNotifyTask; the nested property carries
        // its declared discriminator and the concrete EmailChannel members.
        var json = EverTaskJson.Serialize(task);
        var back = (PolymorphicNotifyTask)EverTaskJson.Deserialize(json, typeof(PolymorphicNotifyTask))!;

        back.Channel.ShouldBeOfType<EmailChannel>();
        ((EmailChannel)back.Channel).Address.ShouldBe("a@b.it");
    }

    [Fact]
    public void Discriminator_is_the_declared_alias_not_a_CLR_type_name()
    {
        var json = EverTaskJson.Serialize(new PolymorphicNotifyTask(new SmsChannel { Number = "+39111" }));

        // SECURITY pin (L33): the wire carries the CLOSED-SET alias "sms" under "$kind", NEVER an
        // assembly-qualified / CLR type name an attacker could weaponize into a deserialization gadget.
        json.ShouldContain("\"$kind\":\"sms\"", Case.Sensitive);
        json.ShouldNotContain("EverTask.Tests");      // no CLR type/assembly name leaks onto the wire
        json.ShouldNotContain("$type");               // not Newtonsoft TypeNameHandling-style type loading
    }

    [Fact]
    public void Unknown_discriminator_does_not_load_an_arbitrary_type()
    {
        // A row whose discriminator is not in the declared set must NOT resolve to some arbitrary type —
        // STJ rejects it (closed-set guarantee). This is the property that keeps the L33 invariant intact.
        const string hostileJson = "{\"Channel\":{\"$kind\":\"System.Diagnostics.Process\",\"Address\":\"x\"}}";

        Should.Throw<System.Text.Json.JsonException>(
            () => EverTaskJson.Deserialize<PolymorphicNotifyTask>(hostileJson));
    }
}
