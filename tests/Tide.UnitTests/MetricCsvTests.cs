using System.Text;
using FluentAssertions;
using Tide.Application.Content;
using Tide.Application.Metrics;

namespace Tide.UnitTests;

public class MetricCsvTests
{
    private static MetricCsvFile Read(string text) => MetricCsv.Read(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ReadsTheColumnsByName_InAnyOrder_IgnoringOthers_WithSpreadsheetRowNumbers()
    {
        var file = Read("Value,Group,Metric_Key,Date\r\n42,Alert queue,alert-queue-open,2026-10-27\r\n\r\n3,,alert-queue-old,2026-10-27\r\n");

        file.Refusal.Should().BeNull();
        file.HasNotes.Should().BeFalse();
        file.Rows.Should().Equal(
            new MetricCsvRow(2, "2026-10-27", "alert-queue-open", "42", null),
            new MetricCsvRow(4, "2026-10-27", "alert-queue-old", "3", null));
    }

    [Fact]
    public void QuotedCells_KeepCommasQuotesAndLineBreaks()
    {
        var file = Read("date,metric_key,value,note\n2026-10-27,q,\"1,204\",\"relay \"\"A\"\", then\nB\"\n2026-10-28,q,5,\n");

        file.Rows.Should().HaveCount(2);
        file.Rows[0].Value.Should().Be("1,204");
        file.Rows[0].Note.Should().Be("relay \"A\", then\nB");
        file.Rows[1].Row.Should().Be(3);
        file.Rows[1].Note.Should().BeEmpty();
    }

    [Fact]
    public void TheMetricsExport_ReadsBackIn_FormulaGuardRemoved()
    {
        var csv = "date,group,metric_key,metric,value,format,direction,retired,note,entered_by\r\n"
                  + $"2026-10-27,Alert queue,alert-queue-open,Open,42,Count,LowerIsBetter,no,{Csv.Cell("-3 after the fix")},Robin\r\n";

        var row = Read(csv).Rows.Single();

        row.Should().Be(new MetricCsvRow(2, "2026-10-27", "alert-queue-open", "42", "-3 after the fix"));
    }

    [Fact]
    public void AByteOrderMark_AndLatin1Text_AreRead()
    {
        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("date,metric_key,value\n2026-10-27,q,1\n")).ToArray();
        MetricCsv.Read(bom).Rows.Should().ContainSingle();

        var latin1 = Encoding.Latin1.GetBytes("date,metric_key,value,note\n2026-10-27,q,1,café\n");
        MetricCsv.Read(latin1).Rows.Single().Note.Should().Be("café");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("day,metric_key,value\n2026-10-27,q,1", "no date column")]
    [InlineData("date,metric,value\n2026-10-27,q,1", "no metric_key column")]
    [InlineData("date,metric_key,value\n2026-10-27,q,\"1", "isn't closed")]
    public void AFileWithoutTheColumnsOrWithAnOpenQuote_IsRefused(string text, string why)
    {
        Read(text).Refusal.Should().Contain(why);
    }

    [Fact]
    public void BinaryOrOversizedOrTooLongFiles_AreRefused()
    {
        MetricCsv.Read([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]).Refusal.Should().Contain("isn't text");
        MetricCsv.Read(new byte[MetricCsv.MaxBytes + 1]).Refusal.Should().Contain("larger than");
        var many = new StringBuilder("date,metric_key,value\n");
        for (var i = 0; i <= MetricCsv.MaxRows; i++) many.Append("2026-10-27,q,1\n");
        Read(many.ToString()).Refusal.Should().Contain("more than");
    }

    [Theory]
    [InlineData("1,204", 1204)]
    [InlineData("97.5%", 97.5)]
    [InlineData(" 3.5h ", 3.5)]
    [InlineData("-2", -2)]
    public void Values_ReadAsTheEntryBoxReadsThem(string text, double expected)
    {
        MetricMath.TryParseValue(text, out var v).Should().BeTrue();
        v.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    [InlineData("1,5e3")]
    [InlineData("100000000000000")]
    public void NonNumbersAndHugeValues_DontRead(string text) => MetricMath.TryParseValue(text, out _).Should().BeFalse();
}
