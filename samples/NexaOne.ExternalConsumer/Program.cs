using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexaFramework.Drivers;
using NexaFramework.Drivers.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDrivers<IExternalProbeDriver>(drivers =>
    drivers.Add<ExternalProbeDriver>(ExternalProbeDriver.DriverId));

using var host = builder.Build();
await host.StartAsync();

var exposedDrivers = host.Services.GetServices<IExternalProbeDriver>().ToArray();
Ensure(
    exposedDrivers.Length == 0,
    "Managed Drivers must remain behind DriverHost instead of being exposed through ordinary DI.");

var driverHost = host.Services.GetRequiredService<DriverHost<IExternalProbeDriver>>();
await driverHost.StartAsync();

var running = driverHost.GetSnapshot().Drivers.Single();
Ensure(running.Descriptor.Id == ExternalProbeDriver.DriverId, "The stable Driver ID changed unexpectedly.");
Ensure(running.State == DriverState.Running, $"Expected Running but received {running.State}.");
Ensure(
    running.Health.Status == DriverHealthStatus.Healthy,
    $"Expected Healthy but received {running.Health.Status}: {running.Health.Message}");

var value = await driverHost.InvokeAsync(
    ExternalProbeDriver.DriverId,
    static (driver, cancellationToken) => driver.ReadAsync(cancellationToken));
Ensure(value == "external-consumer-sample", "The host-mediated product operation returned an unexpected value.");

Console.WriteLine(
    $"External consumer self-check passed: {running.Descriptor.Id} | "
    + $"{running.State}/{running.Health.Status} | {value}");

await driverHost.StopAsync();
await driverHost.CleanupCompletion;
await host.StopAsync();
return 0;

static void Ensure(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("External consumer self-check failed: " + message);
}

internal interface IExternalProbeDriver : IManagedDriver
{
    Task<string> ReadAsync(CancellationToken cancellationToken = default);
}

internal sealed class ExternalProbeDriver : IExternalProbeDriver
{
    public const string DriverId = "external.probe";

    private bool _started;

    public Task StartAsync(
        DriverContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        context.TryReportHealth(new DriverHealth(
            DriverHealthStatus.Healthy,
            "External sample protocol resource is ready."));
        return Task.CompletedTask;
    }

    public Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_started)
            throw new InvalidOperationException("The external sample Driver is not running.");
        return Task.FromResult("external-consumer-sample");
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _started = false;
        return ValueTask.CompletedTask;
    }
}
