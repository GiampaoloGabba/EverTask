using System.Globalization;

using EverTask.Serialization;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Serialization;

/// <summary>
/// Edge cases that historically bite a Newtonsoft→STJ migration: non-ASCII / control characters in
/// strings, culture-sensitive number and date formatting, empty/null members, and decimal precision.
/// </summary>
public class SerializerEdgeCaseTests
{
    private static T Rt<T>(T value) => EverTaskJson.Deserialize<T>(EverTaskJson.Serialize(value))!;

    [Theory]
    [InlineData("Caffè è perché àòù")]
    [InlineData("日本語のテスト")]
    [InlineData("emoji 🚀🔥✅")]
    [InlineData("quotes \" and \\ backslash")]
    [InlineData("tab\tnewline\r\nend")]
    [InlineData("</script> & <html>")]
    public void Strings_with_special_chars_roundtrip(string s) => Rt(s).ShouldBe(s);

    [Fact]
    public void NonAscii_is_written_raw_not_escaped_parity_with_Newtonsoft()
    {
        // Relaxed encoder → non-ASCII stays raw (è, not è), like Newtonsoft. Smaller, readable payloads.
        var json = EverTaskJson.Serialize("Caffè perché");
        json.ShouldContain("Caffè", Case.Sensitive);
        json.ShouldNotContain("\\u00E8");
    }

    [Fact]
    public void Numbers_and_dates_are_culture_invariant()
    {
        // A comma-decimal culture must NOT corrupt JSON numbers/dates. Both Newtonsoft and STJ serialize
        // JSON with the invariant culture; this pins that contract so a host culture can never break recovery.
        var prevCulture   = Thread.CurrentThread.CurrentCulture;
        var prevUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            var it = new CultureInfo("it-IT"); // decimal separator = ','
            Thread.CurrentThread.CurrentCulture   = it;
            Thread.CurrentThread.CurrentUICulture = it;

            var dec = 1234.56m;
            var dbl = 9876.543;
            var dto = new DateTimeOffset(2026, 3, 9, 13, 5, 7, TimeSpan.FromHours(1));

            EverTaskJson.Serialize(dec).ShouldBe("1234.56");
            Rt(dec).ShouldBe(dec);
            Rt(dbl).ShouldBe(dbl);
            Rt(dto).ShouldBe(dto);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture   = prevCulture;
            Thread.CurrentThread.CurrentUICulture = prevUiCulture;
        }
    }

    [Fact]
    public void Empty_and_null_members_roundtrip()
    {
        var original = new ComplexTask(
            OrderId: Guid.Empty, Count: 0, BigNumber: 0, Amount: 0m, Ratio: 0, Enabled: false,
            When: default, Ttl: TimeSpan.Zero, Priority: PocPriority.Low, Note: null,
            Ids: new List<int>(), Tags: Array.Empty<string>(),
            Metadata: new Dictionary<string, string>(), Nested: new NestedDto("", 0));

        var r = Rt(original);
        r.Note.ShouldBeNull();
        r.Ids.ShouldBeEmpty();
        r.Tags.ShouldBeEmpty();
        r.Metadata.ShouldBeEmpty();
        r.Nested.Name.ShouldBe("");
    }

    [Fact]
    public void Decimal_precision_and_trailing_zeros_preserved()
    {
        Rt(0.1m).ShouldBe(0.1m);
        Rt(100.00m).ShouldBe(100.00m);
        Rt(0.000000001m).ShouldBe(0.000000001m);
    }

    [Fact]
    public void Negative_and_extreme_values_roundtrip()
    {
        Rt(int.MinValue).ShouldBe(int.MinValue);
        Rt(long.MinValue).ShouldBe(long.MinValue);
        Rt(decimal.MinValue).ShouldBe(decimal.MinValue);
        Rt(decimal.MaxValue).ShouldBe(decimal.MaxValue);
    }
}
