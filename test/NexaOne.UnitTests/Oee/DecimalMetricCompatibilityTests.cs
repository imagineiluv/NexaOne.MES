using System.Globalization;
using NexaOne.EST.Domain.Oee;
using NexaOne.EST.Domain.Takt;

namespace NexaOne.UnitTests.Oee;

public sealed class DecimalMetricCompatibilityTests
{
    private static readonly DateTime Start = new(2032, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, OeeStateCategory> Categories = new()
    {
        ["RUN"] = new("Productive", true, false, true),
        ["DOWN"] = new("Breakdown", false, true, true),
    };

    // Golden values are verified against the original MES calculators before adopting the shared API.
    // One productive minute and two downtime minutes also retain the product's loss records.
    [Theory]
    [InlineData("0.33335", "0", "60", "0", "0.3333|0.3334|1|0.1111")]
    [InlineData("2.5", "0.625", "12", "0", "0.3333|0.5|0.75|0.125")]
    [InlineData("10", "5", "60", "0", "0.3333|1|0.5|0.1667")]
    [InlineData("1", "-0.25", "30", "0", "0.3333|0.5|1|0.1667")]
    [InlineData("1", "2", "30", "0", "0.3333|0.5|0|0")]
    [InlineData("-1", "0", "30", "0", "0.3333|0|0|0")]
    [InlineData("0", "0", "30", "0", "0.3333|0|0|0")]
    [InlineData("2.5", "0.625", "0", "0", "0.3333|0|0.75|0")]
    [InlineData("2.5", "0.625", "-1", "0", "0.3333|0|0.75|0")]
    [InlineData("2.5", "0.625", "12", "0.5", "1|0.5|0.75|0.375")]
    [InlineData("10000000000000000000", "0", "0.000000000000000001", "0", "0.3333|0.1667|1|0.0556")]
    [InlineData("0.0000000000000000000000000001", "0", "60", "0", "0.3333|0|1|0")]
    public void Oee_preserves_decimal_counts_bounded_ratios_and_rounding(
        string total, string defect, string cycle, string plannedOverride, string expected)
    {
        var result = ComputeOee(D(total), D(defect), D(cycle), D(plannedOverride));
        var values = expected.Split('|').Select(D).ToArray();
        new[] { result.Availability, result.Performance, result.Quality, result.Oee }.Should().Equal(values);
        result.TotalCount.Should().Be(D(total));
        result.DefectCount.Should().Be(D(defect));
        result.OperatingMinutes.Should().Be(1m);
        result.DowntimeMinutes.Should().Be(2m);
        result.Losses.Should().Equal(new OeeLossLine("Breakdown", 2m, Start.AddMinutes(1), Start.AddMinutes(3)));
    }

    [Fact]
    public void Oee_keeps_decimal_overflow_visible_before_clamping()
        => Assert.Throws<OverflowException>(() => ComputeOee(decimal.MaxValue, 0m, 2m, 0m));

    private static OeeResult ComputeOee(decimal total, decimal defect, decimal cycle, decimal plannedOverride)
        => OeeCalculator.Compute(Start, Start.AddMinutes(3),
            [new(Start, "RUN", "RUN"), new(Start.AddMinutes(1), "RUN", "DOWN")],
            new(total, defect), new(cycle, 3m), Categories, new("Unknown", false, false, true), plannedOverride);

    [Theory]
    [InlineData("1", "3", "0.2", "0.1", "0.3333|0.5|0.1667|0.5")]
    [InlineData("1", "3", "0.3", "0.1", "0.3333|0.3333|0|0")]
    [InlineData("1", "1", "2", "1.0001", "1|0.5001|-0.5|-0.49995")]
    [InlineData("1", "1", "0", "0", "1|null|null|null")]
    [InlineData("0.00005", "1", "1", "0.00015", "0.0001|0.0002|0.0001|2")]
    [InlineData("3", "2", "1.25", "0.625", "1.5|0.5|-1|-0.666667")]
    public void Takt_preserves_partial_measurement_nulls_and_unrounded_deviation(
        string net, string required, string measured, string seconds, string expected)
    {
        var result = TaktTimeCalculator.Compute(
            new(D(net), D(required), 1.23445m, "ea", "S/UNIT"),
            new(10.00005m, D(measured), D(seconds), "EA"), 0.1234565m);
        var values = expected.Split('|').Select(v => v == "null" ? (decimal?)null : D(v)).ToArray();
        new[] { result.TargetTaktSecondsPerUnit, result.ActualCycleSecondsPerUnit,
            result.DeviationSecondsPerUnit, result.DeviationRatio }.Should().Equal(values);
        result.IdealCycleSecondsPerUnit.Should().Be(1.2345m);
        result.AvailabilityRatio.Should().Be(0.123457m);
        result.ActualQty.Should().Be(10.0001m);
        result.QuantityUom.Should().Be("ea");
        result.TimeUom.Should().Be("s/unit");
    }

    [Theory]
    [InlineData("target", "target")]
    [InlineData("actual", "actual")]
    [InlineData("net", "NetAvailableSeconds")]
    [InlineData("required", "RequiredQty")]
    [InlineData("ideal", "IdealCycleSecondsPerUnit")]
    [InlineData("time-uom", "TimeUom")]
    [InlineData("quantity-uom", "QuantityUom")]
    [InlineData("negative", "actual")]
    [InlineData("excess-measured", "actual")]
    [InlineData("unpaired", "actual")]
    [InlineData("availability", "oeeAvailabilityRatio")]
    public void Takt_keeps_product_validation_order_and_exception_parameters(string invalid, string parameter)
    {
        var target = new TaktTargetDefinition(1m, 1m, 1m, "EA");
        var actual = new TaktActuals(1m, 1m, 1m, "EA");
        target = invalid switch
        {
            "net" => target with { NetAvailableSeconds = 0m },
            "required" => target with { RequiredQty = 0m },
            "ideal" => target with { IdealCycleSecondsPerUnit = 0m },
            "time-uom" => target with { TimeUom = "ms/unit" },
            _ => target,
        };
        actual = invalid switch
        {
            "quantity-uom" => actual with { QuantityUom = "KG" },
            "negative" => actual with { ActualQty = -1m },
            "excess-measured" => actual with { MeasuredQty = 2m },
            "unpaired" => actual with { ActualRunSeconds = 0m },
            _ => actual,
        };
        var exception = Record.Exception(() => TaktTimeCalculator.Compute(
            invalid == "target" ? null! : target, invalid == "actual" ? null! : actual,
            invalid == "availability" ? 2m : 1m));
        Assert.IsAssignableFrom<ArgumentException>(exception).ParamName.Should().Be(parameter);
        var expectedType = invalid is "target" or "actual" ? typeof(ArgumentNullException)
            : invalid is "net" or "required" or "ideal" or "negative" or "availability"
                ? typeof(ArgumentOutOfRangeException) : typeof(ArgumentException);
        exception!.GetType().Should().Be(expectedType);
    }

    [Fact]
    public void Takt_keeps_decimal_underflow_and_divide_by_zero_behavior()
    {
        var target = new TaktTargetDefinition(0.0000000000000000000000000001m, decimal.MaxValue, 1m, "EA");
        var unmeasured = TaktTimeCalculator.Compute(target, new(1m, 0m, 0m, "EA"), 1m);
        unmeasured.TargetTaktSecondsPerUnit.Should().Be(0m);
        unmeasured.DeviationRatio.Should().BeNull();
        Assert.Throws<DivideByZeroException>(() => TaktTimeCalculator.Compute(target, new(1m, 1m, 1m, "EA"), 1m));
    }

    private static decimal D(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
