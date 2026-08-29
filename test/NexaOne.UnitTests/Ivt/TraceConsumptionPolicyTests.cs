using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;

namespace NexaOne.UnitTests.Ivt;

public sealed class TraceConsumptionPolicyTests
{
    private readonly TraceConsumptionPolicyCatalog _catalog = new();

    [Fact]
    public void Direct_scales_each_persisted_sample()
    {
        var result = _catalog.Evaluate(Item("Direct", 2.5m, scale: 4m), null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Quantity.Should().Be(10m);
        result.Value.AdvanceState.Should().BeTrue();
    }

    [Fact]
    public void Pulse_consumes_only_on_a_rising_edge()
    {
        var at = DateTime.UtcNow;
        var item = Item("Pulse", 1m, at: at, pulseQuantity: 0.25m, scale: 2m);

        var rising = _catalog.Evaluate(item, State(0m, at.AddSeconds(-1)));
        var heldHigh = _catalog.Evaluate(item with { CollectedAt = at.AddSeconds(1) }, State(1m, at));

        rising.Value.Quantity.Should().Be(0.5m);
        heldHigh.Value.Quantity.Should().Be(0m);
        heldHigh.Value.Disposition.Should().Be("NoRisingEdge");
    }

    [Fact]
    public void Counter_delta_baselines_first_sample_and_scales_positive_delta()
    {
        var at = DateTime.UtcNow;
        var first = _catalog.Evaluate(Item("CounterDelta", 10m, at: at), null);
        var next = _catalog.Evaluate(
            Item("CounterDelta", 13.5m, scale: 2m, at: at.AddSeconds(1)),
            State(10m, at));

        first.Value.Quantity.Should().Be(0m);
        first.Value.Disposition.Should().Be("Baseline");
        next.Value.Quantity.Should().Be(7m);
    }

    [Fact]
    public void Counter_reset_rebaselines_without_negative_consumption()
    {
        var at = DateTime.UtcNow;
        var result = _catalog.Evaluate(
            Item("CounterDelta", 2m, at: at),
            State(100m, at.AddSeconds(-1)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Quantity.Should().Be(0m);
        result.Value.AdvanceState.Should().BeTrue();
        result.Value.Disposition.Should().Be("CounterReset");
    }

    [Fact]
    public void Rate_integrate_uses_trapezoidal_area_in_elapsed_hours()
    {
        var at = DateTime.UtcNow;
        var result = _catalog.Evaluate(
            Item("RateIntegrate", 4m, scale: 2m, at: at.AddHours(1)),
            State(2m, at));

        result.Value.Quantity.Should().Be(6m);
        result.Value.AdvanceState.Should().BeTrue();
    }

    [Fact]
    public void Stale_timestamp_is_ignored_without_rewinding_checkpoint()
    {
        var at = DateTime.UtcNow;
        var result = _catalog.Evaluate(
            Item("CounterDelta", 11m, at: at),
            State(10m, at));

        result.Value.Quantity.Should().Be(0m);
        result.Value.AdvanceState.Should().BeFalse();
        result.Value.Disposition.Should().Be("StaleOrDuplicateTimestamp");
    }

    [Fact]
    public void Invalid_mode_is_reported_before_ledger_persistence()
    {
        var result = _catalog.Evaluate(Item("Unknown", 1m), null);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("Direct");
    }

    private static TraceProjectionItem Item(
        string mode,
        decimal value,
        decimal scale = 1m,
        DateTime? at = null,
        decimal? pulseQuantity = null) => new(
        "BINDING", Guid.NewGuid().ToString("N"), "PLANT", "EQ", "PARAM", "FEED",
        mode, scale, pulseQuantity, "kg", value, "Good", at ?? DateTime.UtcNow);

    private static TraceProjectionState State(decimal value, DateTime at) =>
        new("BINDING", Guid.NewGuid().ToString("N"), value, at);
}
