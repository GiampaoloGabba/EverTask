using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Type matrix: every scalar and collection type a task payload may legitimately carry must round-trip
/// through STJ. Proves "STJ supports the type" at the unit level; object-graph composition is covered by
/// <see cref="ComplexPayloadRoundTripTests"/>.
/// </summary>
public class PayloadTypeMatrixTests
{
    private static T Rt<T>(T value) => EverTaskJson.Deserialize<T>(EverTaskJson.Serialize(value))!;

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-7)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Int_roundtrips(int v) => Rt(v).ShouldBe(v);

    [Theory]
    [InlineData(0L)]
    [InlineData(9_000_000_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Long_roundtrips(long v) => Rt(v).ShouldBe(v);

    [Theory]
    [InlineData((short)-32768)]
    [InlineData((byte)255)]
    [InlineData((sbyte)-128)]
    [InlineData((uint)4000000000)]
    [InlineData((ulong)18000000000000000000)]
    public void Integral_widths_roundtrip(object v)
    {
        // Boxed scalar: serialize by runtime type, deserialize back to the same type.
        var json = EverTaskJson.Serialize(v);
        EverTaskJson.Deserialize(json, v.GetType()).ShouldBe(v);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bool_roundtrips(bool v) => Rt(v).ShouldBe(v);

    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('9')]
    [InlineData('€')]
    public void Char_roundtrips(char v) => Rt(v).ShouldBe(v);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3333333333333333)]
    [InlineData(-123456.789)]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void Double_roundtrips(double v) => Rt(v).ShouldBe(v);

    [Fact]
    public void Float_roundtrips() => Rt(3.14159f).ShouldBe(3.14159f);

    [Theory]
    [InlineData("0")]
    [InlineData("1234.56")]
    [InlineData("-0.0000001")]
    [InlineData("79228162514264337593543950335")] // decimal.MaxValue
    public void Decimal_roundtrips(string literal)
    {
        var v = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
        Rt(v).ShouldBe(v);
    }

    [Fact]
    public void String_and_null_roundtrip()
    {
        Rt("hello").ShouldBe("hello");
        Rt("").ShouldBe("");
        Rt<string?>(null).ShouldBeNull();
    }

    [Fact]
    public void Guid_roundtrips()
    {
        var g = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Rt(g).ShouldBe(g);
        Rt(Guid.Empty).ShouldBe(Guid.Empty);
    }

    [Fact]
    public void Enum_roundtrips_as_number()
    {
        Rt(PocPriority.High).ShouldBe(PocPriority.High);
        EverTaskJson.Serialize(PocPriority.High).ShouldBe("2"); // numeric, like Newtonsoft default
    }

    [Fact]
    public void DateTimeOffset_roundtrips_with_offsets()
    {
        foreach (var off in new[] { TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.FromHours(-5), TimeSpan.FromMinutes(330) })
        {
            var v = new DateTimeOffset(2026, 6, 17, 14, 30, 15, off);
            Rt(v).ShouldBe(v);
        }
    }

    [Fact]
    public void DateTimeOffset_preserves_fractional_seconds()
    {
        var v = new DateTimeOffset(2026, 6, 17, 14, 30, 15, TimeSpan.Zero).AddTicks(1234567);
        Rt(v).ShouldBe(v);
    }

    [Fact]
    public void DateTime_utc_roundtrips()
    {
        var v = new DateTime(2026, 6, 17, 8, 0, 0, DateTimeKind.Utc);
        Rt(v).ShouldBe(v);
    }

    [Fact]
    public void DateOnly_TimeOnly_roundtrip()
    {
        Rt(new DateOnly(2026, 12, 31)).ShouldBe(new DateOnly(2026, 12, 31));
        Rt(new TimeOnly(23, 59, 58)).ShouldBe(new TimeOnly(23, 59, 58));
        Rt(new TimeOnly(7, 45)).ShouldBe(new TimeOnly(7, 45));
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("2.01:30:00")]
    [InlineData("-00:05:00")]
    public void TimeSpan_roundtrips(string literal)
    {
        var v = TimeSpan.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
        Rt(v).ShouldBe(v);
    }

    [Fact]
    public void Nullable_value_and_null_roundtrip()
    {
        Rt<int?>(5).ShouldBe(5);
        Rt<int?>(null).ShouldBeNull();
        Rt<DateTimeOffset?>(null).ShouldBeNull();
        Rt<TimeSpan?>(TimeSpan.FromSeconds(10)).ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Collections_roundtrip()
    {
        Rt(new[] { 1, 2, 3 }).ShouldBe(new[] { 1, 2, 3 });
        Rt(new List<string> { "a", "b" }).ShouldBe(new List<string> { "a", "b" });
        Rt(Array.Empty<int>()).ShouldBeEmpty();

        var dict = new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 };
        Rt(dict).ShouldBe(dict);

        var nested = new Dictionary<string, List<int>> { ["a"] = new() { 1, 2 }, ["b"] = new() { 3 } };
        var rn = Rt(nested);
        rn["a"].ShouldBe(new[] { 1, 2 });
        rn["b"].ShouldBe(new[] { 3 });
    }

    [Fact]
    public void Record_with_ctor_and_init_roundtrips()
    {
        var v = new NestedDto("inner", 7) { Flag = true };
        var r = Rt(v);
        r.Name.ShouldBe("inner");
        r.Value.ShouldBe(7);
        r.Flag.ShouldBeTrue();
    }
}
