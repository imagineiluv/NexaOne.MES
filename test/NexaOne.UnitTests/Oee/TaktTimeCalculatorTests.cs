using NexaOne.EST.Domain.Takt;

namespace NexaOne.UnitTests.Oee;

public sealed class TaktTimeCalculatorTests
{
    private static readonly TaktTargetDefinition Target = new(
        NetAvailableSeconds: 28_800m,
        RequiredQty: 800m,
        IdealCycleSecondsPerUnit: 30m,
        QuantityUom: "EA");

    [Fact]
    public void Separates_target_ideal_actual_and_uses_oee_availability_as_the_single_source()
    {
        var result = TaktTimeCalculator.Compute(
            Target,
            new TaktActuals(
                ActualQty: 500m,
                MeasuredQty: 400m,
                ActualRunSeconds: 14_400m,
                QuantityUom: "EA"),
            oeeAvailabilityRatio: 0.875m);

        result.TargetTaktSecondsPerUnit.Should().Be(36m);
        result.IdealCycleSecondsPerUnit.Should().Be(30m);
        result.ActualCycleSecondsPerUnit.Should().Be(36m);
        result.DeviationSecondsPerUnit.Should().Be(0m);
        result.DeviationRatio.Should().Be(0m);
        result.AvailabilityRatio.Should().Be(0.875m);
        result.ActualQty.Should().Be(500m);
        result.MeasuredQty.Should().Be(400m);
        result.QuantityUom.Should().Be("EA");
        result.TimeUom.Should().Be("s/unit");
    }

    [Fact]
    public void Reports_positive_deviation_when_actual_cycle_is_slower_than_target()
    {
        var result = TaktTimeCalculator.Compute(
            Target,
            new TaktActuals(400m, 400m, 18_000m, "EA"),
            oeeAvailabilityRatio: 1m);

        result.TargetTaktSecondsPerUnit.Should().Be(36m);
        result.ActualCycleSecondsPerUnit.Should().Be(45m);
        result.DeviationSecondsPerUnit.Should().Be(9m);
        result.DeviationRatio.Should().Be(0.25m);
    }

    [Fact]
    public void Keeps_trackout_quantity_but_does_not_invent_an_actual_cycle_without_measured_intervals()
    {
        var result = TaktTimeCalculator.Compute(
            Target,
            new TaktActuals(400m, 0m, 0m, "EA"),
            oeeAvailabilityRatio: 0.5m);

        result.ActualQty.Should().Be(400m);
        result.MeasuredQty.Should().Be(0m);
        result.ActualCycleSecondsPerUnit.Should().BeNull();
        result.DeviationSecondsPerUnit.Should().BeNull();
        result.DeviationRatio.Should().BeNull();
    }

    [Fact]
    public void Rejects_target_and_trackout_quantity_uom_mismatch()
    {
        Action act = () => TaktTimeCalculator.Compute(
            Target,
            new TaktActuals(1m, 1m, 30m, "KG"),
            oeeAvailabilityRatio: 1m);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*quantity UOM must match*");
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    public void Rejects_availability_outside_oee_ratio_range(double availability)
    {
        Action act = () => TaktTimeCalculator.Compute(
            Target,
            new TaktActuals(1m, 1m, 30m, "EA"),
            (decimal)availability);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
