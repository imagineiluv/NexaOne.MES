using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server;
using NexaDB.Data.Sqlite;
using NexaFramework;
using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;
using NexaLogic.Plc.AllenBradley;
using NexaLogic.Plc.Hosting;
using NexaLogic.Plc.MitsubishiMc;
using NexaLogic.Plc.ModbusTcp;
using NexaLogic.Plc.OpcUa;
using NexaLogic.Plc.SiemensS7;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>DB, Kafka, and PLC probes share one product-owned diagnostic contract.</summary>
public sealed class ExternalDependencyProbeContractTests
{
    [Fact]
    public async Task Database_and_kafka_probes_resolve_typed_and_report_non_secret_ready_health()
    {
        var dataSource = new EesDataSource
        {
            Provider = new SqliteProvider(),
            ConnectionString = "Data Source=:memory:",
        };
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton<NexaOneDatabaseProbe>();
        services.AddSingleton<IExternalDependencyProbe>(provider =>
            provider.GetRequiredService<NexaOneDatabaseProbe>());
        services.AddSingleton<KafkaBrokerProbe>(_ =>
            new KafkaBrokerProbe(static _ => ValueTask.CompletedTask));
        services.AddSingleton<IExternalDependencyProbe>(provider =>
            provider.GetRequiredService<KafkaBrokerProbe>());
        services.AddSingleton<ExternalDependencyProbeCatalog>();

        await using var provider = services.BuildServiceProvider();
        var database = provider.GetRequiredService<NexaOneDatabaseProbe>();
        var kafka = provider.GetRequiredService<KafkaBrokerProbe>();
        var probes = provider.GetServices<IExternalDependencyProbe>().ToArray();
        probes.Should().ContainSingle(probe => ReferenceEquals(probe, database));
        probes.Should().ContainSingle(probe => ReferenceEquals(probe, kafka));

        var catalog = provider.GetRequiredService<ExternalDependencyProbeCatalog>();
        catalog.Descriptors.Select(descriptor => descriptor.Id).Should().Equal(
            "nexaone.database",
            "nexaone.messaging.kafka");

        var diagnostics = await catalog.CheckAllAsync();
        diagnostics.Should().HaveCount(2);
        var databaseDiagnostic = diagnostics.Single(snapshot => snapshot.Descriptor.Id == "nexaone.database");
        AssertReadyAndSecretFree(
            databaseDiagnostic.Descriptor,
            databaseDiagnostic.Health,
            dataSource.ConnectionString);
        var kafkaDiagnostic = diagnostics.Single(snapshot => snapshot.Descriptor.Id == "nexaone.messaging.kafka");
        AssertReadyAndSecretFree(kafkaDiagnostic.Descriptor, kafkaDiagnostic.Health);
    }

    [Fact]
    public async Task Kafka_actual_broker_probe_reports_sanitized_unhealthy_when_unreachable()
    {
        using var messageBus = new KafkaMessageBus("127.0.0.1:65535");
        var probe = new KafkaBrokerProbe(messageBus);

        var health = await probe.CheckHealthAsync();

        health.Status.Should().Be(ExternalDependencyHealthStatus.Unhealthy);
        AssertSecretFree(probe.Descriptor, health, "127.0.0.1:65535");
    }

    [Fact]
    public async Task Database_failure_diagnostics_do_not_echo_the_connection_string_or_provider_error()
    {
        const string connectionString =
            "Data Source=:memory:;Mode=NotARealMode;Password=super-secret-value";
        var probe = new NexaOneDatabaseProbe(new EesDataSource
        {
            Provider = new SqliteProvider(),
            ConnectionString = connectionString,
        });

        var health = await probe.CheckHealthAsync();

        health.Status.Should().Be(ExternalDependencyHealthStatus.Unhealthy);
        AssertSecretFree(probe.Descriptor, health, connectionString);
    }

    [Fact]
    public async Task Plc_probe_reports_all_five_product_protocols_as_ready()
    {
        IPlcDriver[] drivers =
        {
            new OpcUaDriver(),
            new ModbusTcpDriver(),
            new SiemensS7Driver(),
            new MitsubishiMcDriver(),
            new EtherNetIpDriver(),
        };
        var factory = new PlcDriverFactory(drivers);
        var probe = new FdcPlcProtocolCatalogProbe(factory);

        var health = await probe.CheckHealthAsync();

        probe.Descriptor.Kind.Should().Be("plc");
        probe.Descriptor.Capabilities.Should().Contain(new[]
        {
            "plc-read", "plc-write", "plc-subscription", "multi-protocol-selection",
        });
        drivers.Select(driver => driver.Kind).Should().BeEquivalentTo(new[]
        {
            PlcDriverKind.OpcUa,
            PlcDriverKind.ModbusTcp,
            PlcDriverKind.SiemensS7,
            PlcDriverKind.MitsubishiMc,
            PlcDriverKind.EtherNetIp,
        });
        drivers.Should().OnlyContain(driver =>
            driver.Capabilities.SupportsBatchRead
            && driver.Capabilities.SupportsBatchWrite
            && driver.Capabilities.SupportsSubscriptions
            && driver.Capabilities.SupportsReconnect
            && driver.Capabilities.SupportsQualityState,
            "the five product protocols must preserve the shared read/write/subscription health contract");
        health.Details["registeredProtocols"].Split(',').Should().BeEquivalentTo(new[]
        {
            "OpcUa", "ModbusTcp", "SiemensS7", "MitsubishiMc", "EtherNetIp",
        });
        AssertReadyAndSecretFree(probe.Descriptor, health);
    }

    [Fact]
    public async Task Kafka_blocking_probe_returns_promptly_when_cancelled_mid_probe()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var probe = KafkaMessageBus.RunBlockingProbeAsync(() =>
        {
            entered.Set();
            release.Wait();
            finished.Set();
        }, cancellation.Token);

        try
        {
            entered.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue("the blocking probe must be in flight");
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();

            var cancelled = async () => await probe;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
                "caller cancellation must not wait for the native metadata timeout");
        }
        finally
        {
            release.Set();
            finished.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Required_dependency_health_check_tracks_runtime_failure_after_startup()
    {
        var probe = new MutableDependencyProbe("test.required", Unhealthy());
        var check = new ExternalDependencyHealthCheck(
            new ExternalDependencyProbeCatalog(new[] { probe }));

        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext());
        unhealthy.Status.Should().Be(HealthStatus.Unhealthy);
        unhealthy.Description.Should().Contain("test.required");

        probe.Health = Healthy();
        var recovered = await check.CheckHealthAsync(new HealthCheckContext());
        recovered.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Required_dependency_validation_blocks_module_workers_before_they_start()
    {
        var worker = new RecordingWorker();
        var runtime = new NexaOneMesRuntimeState(
            new ApplicationServer(),
            new ConfigurationBuilder().Build(),
            new IHostedService[] { worker });
        var catalog = new ExternalDependencyProbeCatalog(new IExternalDependencyProbe[]
        {
            new MutableDependencyProbe("test.required", Unhealthy()),
        });
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var startup = new NexaOneMesStartupHostedService(
            provider,
            new TestHostEnvironment(),
            runtime,
            catalog);

        var start = async () => await startup.StartingAsync(CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*before MES workers started*test.required*");
        worker.StartCount.Should().Be(0,
            "required dependency readiness must be established before worker side effects begin");
    }

    private static void AssertReadyAndSecretFree(
        ExternalDependencyDescriptor descriptor,
        ExternalDependencyHealth health,
        params string[] configuredSecrets)
    {
        descriptor.Capabilities.Should().NotBeEmpty();
        descriptor.Capabilities.Should().OnlyHaveUniqueItems();
        health.Status.Should().Be(ExternalDependencyHealthStatus.Healthy);
        AssertSecretFree(descriptor, health, configuredSecrets);
    }

    private static void AssertSecretFree(
        ExternalDependencyDescriptor descriptor,
        ExternalDependencyHealth health,
        params string[] configuredSecrets)
    {
        var diagnosticText = string.Join('\n', new[]
        {
            descriptor.Id,
            descriptor.DisplayName,
            descriptor.Kind,
            descriptor.Version,
            string.Join(',', descriptor.Capabilities),
            health.Summary,
            string.Join(';', health.Details.Select(detail => $"{detail.Key}={detail.Value}")),
        });

        var normalized = diagnosticText.ToUpperInvariant();
        normalized.Should().NotContain("PASSWORD");
        normalized.Should().NotContain("CONNECTIONSTRING");
        normalized.Should().NotContain("SUPER-SECRET-VALUE");
        normalized.Should().NotContain("127.0.0.1");
        normalized.Should().NotContain("65535");
        foreach (var configuredSecret in configuredSecrets)
            diagnosticText.Should().NotContain(configuredSecret);
    }

    private static ExternalDependencyHealth Unhealthy() =>
        new(
            ExternalDependencyHealthStatus.Unhealthy,
            "Dependency is unavailable.",
            DateTimeOffset.UtcNow);

    private static ExternalDependencyHealth Healthy() =>
        new(
            ExternalDependencyHealthStatus.Healthy,
            "Dependency is ready.",
            DateTimeOffset.UtcNow);

    private sealed class MutableDependencyProbe : IExternalDependencyProbe
    {
        public MutableDependencyProbe(string id, ExternalDependencyHealth health)
        {
            Health = health;
            Descriptor = new ExternalDependencyDescriptor(
                id,
                "Test required dependency",
                "test",
                "1.0.0",
                ["health-probe"]);
        }

        public ExternalDependencyDescriptor Descriptor { get; }
        public ExternalDependencyHealth Health { get; set; }

        public ValueTask<ExternalDependencyHealth> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Health);
        }
    }

    private sealed class RecordingWorker : IHostedService
    {
        public int StartCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NexaOne.ServerTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
