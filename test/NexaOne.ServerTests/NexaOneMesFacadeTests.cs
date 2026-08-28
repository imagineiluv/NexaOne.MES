using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NexaOne.Application.Query;
using NexaOne.Server;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.Web.Services.Meta;
using NexaFramework;
using NexaFramework.Scheduling;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class NexaOneMesFacadeTests
{
    private const string JwtSecret = "nexaone-mes-facade-test-secret-at-least-32-bytes-long";

    [Fact]
    public void Add_facade_returns_the_same_service_collection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = Configuration("Data Source=:memory:");

        var returned = services.AddNexaOneMes(
            configuration,
            options => options.DiagnosticsPath = "/architecture");

        returned.Should().BeSameAs(services);
        services.Count(descriptor => descriptor.ServiceType == typeof(ApplicationServer)).Should().Be(1,
            "framework and MES must share one process-wide ApplicationServer registration");
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IRecurringScheduler),
            "modules-OFF preserves the existing manual-batch-only behavior");

        using var provider = services.BuildServiceProvider(validateScopes: true);
        provider.GetRequiredService<IQueryRegistry>().Should().NotBeNull();
        provider.GetRequiredService<NexaOneMesHostingOptions>().DiagnosticsPath.Should().Be("/architecture");
        var runtime = provider.GetRequiredService<NexaOneMesRuntimeState>();
        runtime.ModulesEnabled.Should().BeFalse();
        runtime.WorkerCount.Should().Be(0);
        var bridgeCatalog = provider.GetRequiredService<INexaModuleBridgeCatalog>();
        var declaredBridges = NexaOneMesBridgeCatalog
            .Create()
            .Descriptors;
        bridgeCatalog.Descriptors.Should().Equal(declaredBridges,
            "modules-OFF에서도 계약 메타데이터는 검증하되 Spring Bridge 인스턴스는 만들지 않는다");
        services.Should().NotContain(descriptor =>
            bridgeCatalog.Descriptors.Any(bridge => bridge.ContractType == descriptor.ServiceType));
    }

    [Fact]
    public void Add_facade_preserves_project_specific_master_directory_adapters()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var equipment = Mock.Of<IEquipmentDirectory>();
        var vendor = Mock.Of<IVendorDirectory>();
        var identity = Mock.Of<IMaintenanceIdentityDirectory>();
        services.AddSingleton(equipment);
        services.AddSingleton(vendor);
        services.AddSingleton(identity);

        var settings = Settings("Data Source=:memory:");
        settings["Server:Modules:Enabled"] = "true";
        services.AddNexaOneMes(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        using var provider = services.BuildServiceProvider(validateScopes: true);
        provider.GetRequiredService<IEquipmentDirectory>().Should().BeSameAs(equipment);
        provider.GetRequiredService<IVendorDirectory>().Should().BeSameAs(vendor);
        provider.GetRequiredService<IMaintenanceIdentityDirectory>().Should().BeSameAs(identity);
        services.Count(descriptor => descriptor.ServiceType == typeof(IEquipmentDirectory)).Should().Be(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(IVendorDirectory)).Should().Be(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(IMaintenanceIdentityDirectory)).Should().Be(1);
    }

    [Fact]
    public void Add_facade_auto_registers_every_declared_module_bridge_contract()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var settings = Settings("Data Source=:memory:");
        settings["Server:Modules:Enabled"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        services.AddNexaOneMes(configuration);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var catalog = provider.GetRequiredService<INexaModuleBridgeCatalog>();
        var declaredBridges = NexaOneMesBridgeCatalog
            .Create()
            .Descriptors;
        catalog.Descriptors.Should().Equal(declaredBridges);
        catalog.Descriptors.Select(descriptor => descriptor.ContractType)
            .Should().OnlyHaveUniqueItems();
        foreach (var descriptor in catalog.Descriptors)
        {
            services.Count(service => service.ServiceType == descriptor.ContractType).Should().Be(1,
                $"{descriptor.ContractType.Name}은 host composition catalog에서 한 번만 등록돼야 한다");
        }
    }

    [Fact]
    public void Add_facade_rejects_duplicate_registration_before_side_effects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = Configuration("Data Source=:memory:");
        services.AddNexaOneMes(configuration);

        var second = () => services.AddNexaOneMes(configuration);

        second.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddNexaOneMes()*only be called once*");
    }

    [Fact]
    public void Invalid_hosting_options_do_not_poison_the_service_collection_marker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = Configuration("Data Source=:memory:");

        var invalid = () => services.AddNexaOneMes(
            configuration,
            options => options.HealthPath = "relative");

        invalid.Should().Throw<ArgumentException>().WithMessage("*HealthPath*absolute application path*");
        var retry = () => services.AddNexaOneMes(configuration);
        retry.Should().NotThrow("failed option validation must not mark MES as registered");
    }

    [Fact]
    public void Add_and_build_defer_spring_bootstrap_until_host_start()
    {
        var settings = Settings("Data Source=:memory:");
        settings["Server:Modules:Enabled"] = "true";
        settings["Server:SpringConfig"] = Path.Combine(
            Path.GetTempPath(), $"missing-spring-{Guid.NewGuid():N}.xml");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var registration = () => builder.Services.AddNexaOneMes(builder.Configuration);

        registration.Should().NotThrow("service registration must not read or boot Spring configuration");
        using var app = builder.Build();
        var runtime = app.Services.GetRequiredService<NexaOneMesRuntimeState>();
        runtime.ModulesEnabled.Should().BeTrue();
        runtime.LoadedServices.Should().BeEmpty();
        runtime.WorkerCount.Should().Be(0);

        var prematureBridge = () => app.Services.GetRequiredService<IMrpBridge>();
        prematureBridge.Should().Throw<InvalidOperationException>().WithMessage("*runtime is not started*");
    }

    [Fact]
    public async Task Production_modules_off_fresh_sqlite_is_initialized_before_admin_hardening()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), $"nexaone-production-fresh-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings(
            $"Data Source={databasePath};Foreign Keys=False"));
        builder.Services.AddNexaOneMes(builder.Configuration);

        await using var app = builder.Build();
        app.UseNexaOneMes();

        try
        {
            await app.StartAsync();

            await using var connection = new SqliteConnection(
                $"Data Source={databasePath};Foreign Keys=False");
            await connection.OpenAsync();
            Scalar(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SYS_USER'")
                .Should().Be(1, "the schema phase must precede Production default-admin hardening");

            await app.StopAsync();
        }
        finally
        {
            try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Mes_preserves_but_does_not_stop_an_unstarted_host_scheduler_override()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        var configuration = Configuration("Data Source=:memory:");
        var scheduler = new CustomScheduler();

        services.AddSingleton<IRecurringScheduler>(scheduler);
        services.AddNexaOneMes(configuration);

        services.Count(descriptor => descriptor.ServiceType == typeof(IRecurringScheduler)).Should().Be(1,
            "MES should remove only the framework default, not a host-provided scheduler");
        using var provider = services.BuildServiceProvider(validateScopes: true);
        provider.GetRequiredService<IRecurringScheduler>().Should().BeSameAs(scheduler);
        var worker = provider.GetServices<IHostedService>().OfType<BatchProcessWorker>().Single();

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        scheduler.StartCount.Should().Be(0, "disabled batch processing must not start a host-owned scheduler");
        scheduler.StopCount.Should().Be(0, "a scheduler not started by the worker must remain host-owned");
    }

    [Theory]
    [InlineData(BackgroundServiceExceptionBehavior.StopHost, true)]
    [InlineData(BackgroundServiceExceptionBehavior.Ignore, false)]
    public async Task Deferred_module_background_faults_follow_the_generic_host_policy(
        BackgroundServiceExceptionBehavior behavior,
        bool shouldStopHost)
    {
        using var worker = new DelayedFaultWorker();
        var lifetime = new RecordingHostLifetime();
        await worker.StartAsync(CancellationToken.None);
        worker.ExecuteTask.Should().NotBeNull("the delayed worker returned from StartAsync before failing");

        var monitor = NexaOneMesRuntimeState.ObserveBackgroundServiceAsync(
            worker.ExecuteTask!,
            worker.GetType(),
            lifetime,
            behavior,
            NullLogger.Instance);
        worker.FailAfterStartup();
        await monitor;

        lifetime.StopCount.Should().Be(shouldStopHost ? 1 : 0);
    }

    [Fact]
    public async Task Deferred_module_workers_keep_serial_start_and_reverse_stop_by_default()
    {
        var events = new List<string>();
        var workers = new IHostedService[]
        {
            new RecordingWorker("A", events),
            new RecordingWorker("B", events),
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<HostOptions>(options =>
        {
            options.ServicesStartConcurrently = false;
            options.ServicesStopConcurrently = false;
        });
        using var provider = services.BuildServiceProvider();
        var runtime = new NexaOneMesRuntimeState(
            new ApplicationServer(), Configuration("Data Source=:memory:"), workers);

        await runtime.StartWorkersAsync(provider, CancellationToken.None);
        await runtime.StopAsync(CancellationToken.None);

        events.Should().Equal("start:A", "start:B", "stop:B", "stop:A");
    }

    [Fact]
    public async Task Deferred_module_workers_honor_concurrent_host_start_and_stop()
    {
        var coordinator = new ConcurrentWorkerCoordinator(expectedWorkers: 2);
        var workers = new IHostedService[]
        {
            new CoordinatedWorker("A", coordinator),
            new CoordinatedWorker("B", coordinator),
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<HostOptions>(options =>
        {
            options.ServicesStartConcurrently = true;
            options.ServicesStopConcurrently = true;
        });
        using var provider = services.BuildServiceProvider();
        var runtime = new NexaOneMesRuntimeState(
            new ApplicationServer(), Configuration("Data Source=:memory:"), workers);

        await runtime.StartWorkersAsync(provider, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Started.Should().Be(2);
        coordinator.Stopped.Should().Be(2);
        coordinator.StopOrder.Should().Equal(new[] { "B", "A" },
            "Generic Host invokes concurrent StopAsync methods in reverse registration order");
    }

    [Fact]
    public async Task Use_facade_maps_host_blazor_mobile_pop_portal_and_operational_endpoints()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"nexaone-mes-facade-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings($"Data Source={databasePath};Foreign Keys=False"));
        builder.Services.AddNexaOneMes(builder.Configuration, options =>
        {
            options.HealthPath = "/ready";
            options.DiagnosticsPath = "/architecture";
            options.RealtimeHubPath = "/realtime/mes";
        });

        await using var app = builder.Build();
        app.UseNexaOneMes();

        var secondUse = () => app.UseNexaOneMes();
        secondUse.Should().Throw<InvalidOperationException>()
            .WithMessage("*UseNexaOneMes()*only be called once*");

        var patterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        patterns.Should().Contain(new[]
        {
            "/", "/ready", "/architecture", "/realtime/mes",
            "/Mobile", "/Mobile/{UiId}", "/POP", "/POP/{UiId}",
            "/Designer/{*path:nonfile}", "/spa/{*path:nonfile}",
        });

        await app.StartAsync();
        using var client = app.GetTestClient();
        var negotiate = await client.PostAsync(
            $"/realtime/mes/negotiate?negotiateVersion=1&access_token={MintToken()}",
            content: null);
        negotiate.StatusCode.Should().Be(HttpStatusCode.OK,
            "the configured hub path must also be used by JWT query-token extraction");

        await app.StopAsync();

        try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Development_database_initializer_keeps_schema_seed_and_hierarchy_idempotent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"nexaone-mes-seed-{Guid.NewGuid():N}.db");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(Settings($"Data Source={databasePath};Foreign Keys=False"));

        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=False"))
        {
            await connection.OpenAsync();
            Scalar(connection, "SELECT COUNT(*) FROM SYS_SCREEN_DEFINITION").Should().Be(3);
            Scalar(connection, "SELECT COUNT(*) FROM SYS_SCREEN_TARGET").Should().Be(3);
            Scalar(connection, "SELECT COUNT(*) FROM SYS_SCREEN_DEFINITION d " +
                               "JOIN SYS_SCREEN_TARGET t ON t.UI_ID=d.UI_ID WHERE " +
                               "(d.UI_ID='POM_MES_WORK_EXECUTION' AND d.TITLE='MES 작업 실행' AND t.TARGET_CHANNEL='MES' AND t.ENTRY_PATH='/meta/POM_MES_WORK_EXECUTION') OR " +
                               "(d.UI_ID='POM_MOBILE_WORK_EXECUTION' AND d.TITLE='모바일 작업 실행' AND t.TARGET_CHANNEL='MOBILE' AND t.ENTRY_PATH='/Mobile/POM_MOBILE_WORK_EXECUTION') OR " +
                               "(d.UI_ID='POM_POP_WORK_EXECUTION' AND d.TITLE='POP 작업 실행' AND t.TARGET_CHANNEL='POP' AND t.ENTRY_PATH='/POP/POM_POP_WORK_EXECUTION')")
                .Should().Be(3);

            foreach (var uiId in new[]
                     {
                         "POM_MES_WORK_EXECUTION",
                         "POM_MOBILE_WORK_EXECUTION",
                         "POM_POP_WORK_EXECUTION",
                     })
            {
                var definition = ReadScreenDefinition(connection, uiId);
                definition.Layout.Should().NotBeNull("신규 작업실행 화면은 빈 정의가 아닌 공통 템플릿이어야 한다");
            }
        }

        NexaOneDevelopmentDatabaseInitializer.Initialize(app);

        await using (var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=False"))
        {
            await connection.OpenAsync();
            Scalar(connection, "SELECT COUNT(*) FROM SYS_SCREEN_DEFINITION")
                .Should().Be(3, "두 번째 초기화에서도 작업 화면 정의가 중복되면 안 된다");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_SCREEN_TARGET")
                .Should().Be(3, "두 번째 초기화에서도 화면 진입 대상이 중복되면 안 된다");
            Scalar(connection, "SELECT COUNT(*) FROM SYS_MENU").Should().BeGreaterThan(0);
            Scalar(connection, "SELECT COUNT(*) FROM MDM_PLANT").Should().Be(2);
            Scalar(connection, "SELECT COUNT(*) FROM POM_PRODUCTION_ORDER WHERE PLAN_ID='PPLAN01'").Should().Be(2);
            Scalar(connection, "SELECT COUNT(*) FROM POM_WORK_ORDER WHERE PRODUCTION_ORDER_ID IN ('PORD01','PORD02')")
                .Should().Be(2);
            Scalar(connection, "SELECT COUNT(*) FROM SYS_BATCH_PROCESS").Should().Be(2);
        }

        try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("POM_MES_WORK_EXECUTION", "MES 작업 실행")]
    [InlineData("POM_MOBILE_WORK_EXECUTION", "모바일 작업 실행")]
    [InlineData("POM_POP_WORK_EXECUTION", "POP 작업 실행")]
    public void Work_execution_screen_template_exposes_shared_queries_fields_and_bridge_commands(
        string uiId,
        string title)
    {
        var definition = PomWorkExecutionScreenTemplate.Create(uiId, title);
        var nodes = DescendantsAndSelf(definition.Layout!).ToList();

        definition.UiId.Should().Be(uiId);
        definition.Title.Should().Be(title);
        definition.Purpose.Should().Be(ScreenPurpose.Execute);
        definition.ReadRequiredPermission.Should().Be("pom:read");
        definition.Fields.Should().ContainSingle(field =>
            field.Key == PomWorkExecutionScreenTemplate.TemplateRevisionField && field.Hidden);
        nodes.OfType<GridWidget>().Select(grid => grid.QueryId).Should().Equal(
            "POM.LotRoutingContextList",
            "POM.RouteExceptionList",
            "POM.RouteDeviationTimeline",
            "POM.LotDefectExecutionList",
            "POM.WorkOrderList",
            "POM.WorkOrderExecutionList");

        var fields = nodes.OfType<FormWidget>().SelectMany(form => form.Fields ?? [])
            .Select(widget => widget.Field!)
            .ToList();
        fields.Select(field => field.Key).Should().Contain(new[]
        {
            "LOT_ID", "PLANT_ID", "CONTROL_MODE", "CURRENT_STEP", "CURRENT_PROCESS_ID",
            "NEXT_STEP", "NEXT_PROCESS_ID", "CONTROL_MODE_TARGET", "CONTROL_MODE_REASON",
            "DEVIATION_TYPE", "TARGET_STEP_INDEX", "REASON",
            "EXCEPTION_ID", "REVIEW_REASON",
            "WORK_ORDER_ID", "PRODUCTION_ORDER_ID", "PRODUCT_ID", "PROCESS_ID",
            "ROUTING_SCOPE", "ROUTING_ID", "ROUTING_STEP_NO",
            "WORK_CENTER_ID", "EQUIPMENT_ID", "OWNER_ID", "STATUS", "VERSION_NO",
            "COMPLETE_QTY", "SCRAP_QTY", "goodQty", "defectQty", "remark",
        });

        nodes.OfType<ButtonWidget>().Select(button => button.Command).Should().Equal(
            "bridge:pom.lot.track-in",
            "bridge:pom.lot.track-out",
            "bridge:pom.route.control-mode.change",
            "bridge:pom.route.exception.request",
            "bridge:pom.route.exception.approve",
            "bridge:pom.route.exception.reject",
            "bridge:pom.route.deviation.apply",
            "bridge:pom.work-order.start",
            "bridge:pom.work-order.report",
            "bridge:pom.work-order.hold",
            "bridge:pom.work-order.release-hold",
            "bridge:pom.work-order.complete");

        nodes.OfType<ButtonWidget>().Single(button => button.Command == "bridge:pom.route.exception.request")
            .RequiredPermission.Should().Be("pom:routing.request");
        nodes.OfType<ButtonWidget>().Single(button => button.Command == "bridge:pom.route.control-mode.change")
            .RequiredPermission.Should().Be("pom:manage");
        nodes.OfType<ButtonWidget>().Where(button => button.Command is
                "bridge:pom.route.exception.approve" or "bridge:pom.route.exception.reject")
            .Should().OnlyContain(button => button.RequiredPermission == "pom:routing.approve");

        fields.Single(field => field.Key == "DEVIATION_TYPE").Options
            .Should().Equal("Bypass", "Alternative", "SequenceChange", "Rework");
        definition.SearchFields.Should().ContainSingle(field => field.Key == "routingScope")
            .Which.Options.Should().Equal("Unbound", "Operation", "SerialRoute");
        nodes.OfType<GridWidget>().Single(grid => grid.QueryId == "POM.WorkOrderList")
            .Columns!.Should().ContainSingle(column => column.Key == "ROUTING_SCOPE");

        var defectCollection = nodes.OfType<CollectionWidget>().Should().ContainSingle().Subject;
        defectCollection.CollectionKey.Should().Be("DEFECTS");
        defectCollection.BindingScope.Should().Be("lot");
        defectCollection.Fields!.Select(field => field.Field!.Key)
            .Should().Equal("DEFECT_CODE", "DEFECT_QTY");
        nodes.OfType<GridWidget>().Single(grid => grid.QueryId == "POM.LotDefectExecutionList")
            .SelectionDisabled.Should().BeTrue();
    }

    [Fact]
    public void Work_execution_screen_template_recognizes_only_structurally_empty_canonical_definitions()
    {
        const string uiId = "POM_MES_WORK_EXECUTION";
        var emptyLayoutJson = ScreenDefinitionJson.Serialize(new ScreenDefinition(
            uiId,
            "MES 작업 실행",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode { Id = "empty", Children = Array.Empty<LayoutNode>() }));

        PomWorkExecutionScreenTemplate.IsEmptyCanonicalDefinition(uiId, emptyLayoutJson).Should().BeTrue();
        var configuredRefreshJson = ScreenDefinitionJson.Serialize(new ScreenDefinition(
            uiId,
            "MES 작업 실행",
            Array.Empty<FieldDefinition>(),
            RefreshIntervalSeconds: 15));
        var configuredPermissionJson = ScreenDefinitionJson.Serialize(new ScreenDefinition(
            uiId,
            "MES 작업 실행",
            Array.Empty<FieldDefinition>(),
            ReadRequiredPermission: "pom:read"));
        PomWorkExecutionScreenTemplate.IsEmptyCanonicalDefinition(uiId, configuredPermissionJson).Should().BeFalse(
            "Designer에서 지정한 화면 권한은 비어 있는 정의로 간주해 덮어쓰면 안 된다");
        PomWorkExecutionScreenTemplate.IsEmptyCanonicalDefinition(uiId, configuredRefreshJson).Should().BeFalse(
            "사용자가 지정한 자동 새로고침 설정도 화면 사용자 정의로 보존해야 한다");
        PomWorkExecutionScreenTemplate.IsEmptyCanonicalDefinition(
                uiId,
                ScreenDefinitionJson.Serialize(new ScreenDefinition(
                    uiId,
                    "MES 작업 실행",
                    Array.Empty<FieldDefinition>(),
                    Purpose: ScreenPurpose.Execute)))
            .Should().BeFalse("사용자가 지정한 화면 목적도 Designer 사용자 정의로 보존해야 한다");
        PomWorkExecutionScreenTemplate.IsEmptyCanonicalDefinition(
                uiId,
                "{\"uiId\":\"POM_MES_WORK_EXECUTION\",\"title\":\"사용자 정의\",\"fields\":[]," +
                "\"layout\":null,\"futureWidget\":{}}")
            .Should().BeFalse("알 수 없는 미래 Designer 속성을 빈 정의로 오판하면 안 된다");
        PomWorkExecutionScreenTemplate.IsEmptyCanonicalDefinition(
                uiId,
                "{\"uiId\":\"POM_MES_WORK_EXECUTION\",\"title\":\"사용자 정의\",\"fields\":[]," +
                "\"layout\":{\"kind\":\"futureWidget\"}}")
            .Should().BeFalse("역직렬화할 수 없는 사용자 layout을 덮어쓰면 안 된다");
    }

    [Fact]
    public void Work_execution_screen_template_upgrades_only_exact_managed_revisions()
    {
        const string uiId = "POM_MES_WORK_EXECUTION";
        const string title = "현장 MES 작업 실행";
        var legacyJson = ScreenDefinitionJson.Serialize(
            PomWorkExecutionScreenTemplate.CreateLegacyRevision1(uiId, title));
        var revision2Json = ScreenDefinitionJson.Serialize(
            PomWorkExecutionScreenTemplate.CreateLegacyRevision2(uiId, title));
        var revision3 = PomWorkExecutionScreenTemplate.CreateLegacyRevision3(uiId, title);
        var revision3Json = ScreenDefinitionJson.Serialize(revision3);
        var revision4 = PomWorkExecutionScreenTemplate.CreateLegacyRevision4(uiId, title);
        var revision4Json = ScreenDefinitionJson.Serialize(revision4);
        var currentJson = PomWorkExecutionScreenTemplate.Serialize(uiId, title);

        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(uiId, legacyJson).Should().BeTrue();
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(uiId, revision2Json).Should().BeTrue();
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(uiId, revision3Json).Should().BeTrue();
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(uiId, revision4Json).Should().BeTrue();
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(uiId, currentJson).Should().BeTrue();

        var customized = PomWorkExecutionScreenTemplate.CreateLegacyRevision1(uiId, title) with
        {
            RefreshIntervalSeconds = 15,
        };
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                uiId, ScreenDefinitionJson.Serialize(customized))
            .Should().BeFalse("이전 템플릿에서 사용자가 바꾼 속성은 자동 업그레이드로 덮어쓰면 안 된다");

        var customizedRevision2 = PomWorkExecutionScreenTemplate.CreateLegacyRevision2(uiId, title) with
        {
            RefreshIntervalSeconds = 30,
        };
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                uiId, ScreenDefinitionJson.Serialize(customizedRevision2))
            .Should().BeFalse("revision 2 화면에서 사용자가 바꾼 속성도 자동 업그레이드로 덮어쓰면 안 된다");

        var revision3Nodes = DescendantsAndSelf(revision3.Layout!).ToList();
        revision3Nodes.OfType<CollectionWidget>().Should().BeEmpty();
        revision3Nodes.OfType<GridWidget>().Should().OnlyContain(grid =>
            string.IsNullOrWhiteSpace(grid.SelectionScope) && !grid.SelectionDisabled);
        revision3Nodes.OfType<FormWidget>().Should().OnlyContain(form =>
            string.IsNullOrWhiteSpace(form.BindingScope));

        var customizedRevision3 = revision3 with { RefreshIntervalSeconds = 45 };
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                uiId, ScreenDefinitionJson.Serialize(customizedRevision3))
            .Should().BeFalse("revision 3 화면의 Designer 변경도 자동 업그레이드로 덮어쓰면 안 된다");

        var revision4Nodes = DescendantsAndSelf(revision4.Layout!).ToList();
        revision4.SearchFields.Should().NotContain(field => field.Key == "routingScope");
        revision4Nodes.OfType<GridWidget>().Single(grid => grid.QueryId == "POM.WorkOrderList")
            .Columns!.Should().NotContain(column => column.Key == "ROUTING_SCOPE");
        var customizedRevision4 = revision4 with { RefreshIntervalSeconds = 60 };
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                uiId, ScreenDefinitionJson.Serialize(customizedRevision4))
            .Should().BeFalse("revision 4 화면의 Designer 변경도 자동 업그레이드로 덮어쓰면 안 된다");
    }

    [Fact]
    public void Work_execution_revision1_uses_frozen_golden_fingerprint_fixture()
    {
        var path = RepositorySource.GetFile(
            "test", "contract", "pom-work-execution-revision1-golden.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));

        fixture.RootElement.GetProperty("sha256").GetString()
            .Should().Be(PomWorkExecutionScreenTemplate.LegacyRevision1GoldenSha256);
    }

    [Fact]
    public void Work_execution_revision3_uses_frozen_golden_fingerprint_fixture()
    {
        var path = RepositorySource.GetFile(
            "test", "contract", "pom-work-execution-revision3-golden.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));

        fixture.RootElement.GetProperty("sha256").GetString()
            .Should().Be(PomWorkExecutionScreenTemplate.LegacyRevision3GoldenSha256);
        PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                "POM_MES_WORK_EXECUTION",
                ScreenDefinitionJson.Serialize(PomWorkExecutionScreenTemplate.CreateLegacyRevision3(
                    "POM_MES_WORK_EXECUTION", "MES 작업 실행")))
            .Should().BeTrue("revision 3 recognition must be driven by the frozen fingerprint");
    }

    [Fact]
    public async Task Development_database_initializer_preserves_designer_customizations()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"nexaone-mes-custom-screen-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Foreign Keys=False";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(Settings(connectionString));

        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(connectionString);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            await using (var definition = connection.CreateCommand())
            {
                definition.Transaction = transaction;
                definition.CommandText = @"INSERT INTO SYS_SCREEN_DEFINITION
                    (UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES ('POM_MES_WORK_EXECUTION', @title, @json, 'designer-user', @now, 'designer-user', @now)";
                definition.Parameters.AddWithValue("@title", "사용자 MES 작업 화면");
                definition.Parameters.AddWithValue("@json", "{\"uiId\":\"POM_MES_WORK_EXECUTION\",\"fields\":[{\"name\":\"customField\"}]}");
                definition.Parameters.AddWithValue("@now", "2026-07-14T00:00:00.0000000Z");
                await definition.ExecuteNonQueryAsync();
            }

            await using (var target = connection.CreateCommand())
            {
                target.Transaction = transaction;
                target.CommandText = @"INSERT INTO SYS_SCREEN_TARGET
                    (UI_ID, TARGET_CHANNEL, ENTRY_PATH, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES ('POM_MES_WORK_EXECUTION', 'MES', '/meta/CUSTOM_MES_WORK_EXECUTION',
                            'designer-user', @now, 'designer-user', @now)";
                target.Parameters.AddWithValue("@now", "2026-07-14T00:00:00.0000000Z");
                await target.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT d.TITLE, d.DEFINITION_JSON,
                                           t.TARGET_CHANNEL, t.ENTRY_PATH, t.CREATED_BY, t.UPDATED_BY
                                    FROM SYS_SCREEN_DEFINITION d
                                    JOIN SYS_SCREEN_TARGET t ON t.UI_ID = d.UI_ID
                                    WHERE d.UI_ID = 'POM_MES_WORK_EXECUTION'";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("사용자 MES 작업 화면");
            reader.GetString(1).Should().Be("{\"uiId\":\"POM_MES_WORK_EXECUTION\",\"fields\":[{\"name\":\"customField\"}]}");
            reader.GetString(2).Should().Be("MES");
            reader.GetString(3).Should().Be("/meta/CUSTOM_MES_WORK_EXECUTION");
            reader.GetString(4).Should().Be("designer-user");
            reader.GetString(5).Should().Be("designer-user");
        }

        try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Development_database_initializer_upgrades_managed_revision_and_preserves_metadata()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"nexaone-mes-empty-screen-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Foreign Keys=False";
        const string customizedTitle = "현장 MES 작업 실행";
        const string customizedPath = "/meta/CUSTOM_FLOOR_EXECUTION";
        const string auditTime = "2026-07-14T01:02:03.0000000Z";
        var legacyCanonicalJson = ScreenDefinitionJson.Serialize(
            PomWorkExecutionScreenTemplate.CreateLegacyRevision1(
                "POM_MES_WORK_EXECUTION", customizedTitle));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(Settings(connectionString));

        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(connectionString);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            await using (var definition = connection.CreateCommand())
            {
                definition.Transaction = transaction;
                definition.CommandText = @"INSERT INTO SYS_SCREEN_DEFINITION
                    (UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES ('POM_MES_WORK_EXECUTION', @title, @json, 'designer-user', @now, 'designer-user', @now)";
                definition.Parameters.AddWithValue("@title", customizedTitle);
                definition.Parameters.AddWithValue("@json", legacyCanonicalJson);
                definition.Parameters.AddWithValue("@now", auditTime);
                await definition.ExecuteNonQueryAsync();
            }

            await using (var target = connection.CreateCommand())
            {
                target.Transaction = transaction;
                target.CommandText = @"INSERT INTO SYS_SCREEN_TARGET
                    (UI_ID, TARGET_CHANNEL, ENTRY_PATH, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES ('POM_MES_WORK_EXECUTION', 'MES', @entryPath,
                            'designer-user', @now, 'designer-user', @now)";
                target.Parameters.AddWithValue("@entryPath", customizedPath);
                target.Parameters.AddWithValue("@now", auditTime);
                await target.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT d.TITLE, d.DEFINITION_JSON,
                                           d.CREATED_BY, d.CREATED_AT, d.UPDATED_BY, d.UPDATED_AT,
                                           t.TARGET_CHANNEL, t.ENTRY_PATH,
                                           t.CREATED_BY, t.CREATED_AT, t.UPDATED_BY, t.UPDATED_AT
                                    FROM SYS_SCREEN_DEFINITION d
                                    JOIN SYS_SCREEN_TARGET t ON t.UI_ID = d.UI_ID
                                    WHERE d.UI_ID = 'POM_MES_WORK_EXECUTION'";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            reader.GetString(0).Should().Be(customizedTitle);
            var upgraded = ScreenDefinitionJson.Deserialize(reader.GetString(1));
            upgraded.Should().NotBeNull();
            upgraded!.Title.Should().Be(customizedTitle,
                "업그레이드된 JSON도 기존 Designer 제목을 사용해야 한다");
            upgraded.Layout.Should().NotBeNull();
            upgraded.Purpose.Should().Be(ScreenPurpose.Execute);
            upgraded.Fields.Should().ContainSingle(field =>
                field.Key == PomWorkExecutionScreenTemplate.TemplateRevisionField && field.Hidden);
            DescendantsAndSelf(upgraded.Layout!).OfType<GridWidget>().Select(grid => grid.QueryId)
                .Should().Contain("POM.LotRoutingContextList");
            reader.GetString(2).Should().Be("designer-user");
            reader.GetString(3).Should().Be(auditTime);
            reader.GetString(4).Should().Be("designer-user");
            reader.GetString(5).Should().Be(auditTime);
            reader.GetString(6).Should().Be("MES");
            reader.GetString(7).Should().Be(customizedPath);
            reader.GetString(8).Should().Be("designer-user");
            reader.GetString(9).Should().Be(auditTime);
            reader.GetString(10).Should().Be("designer-user");
            reader.GetString(11).Should().Be(auditTime);
        }

        try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Development_database_initializer_replaces_managed_legacy_ui_id_placeholder_title()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"nexaone-mes-placeholder-title-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Foreign Keys=False";
        const string uiId = "POM_MES_WORK_EXECUTION";
        const string auditTime = "2026-07-14T03:21:09.0000000Z";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(Settings(connectionString));

        NexaOne.Infrastructure.Persistence.SqliteSchemaInitializer.EnsureSchema(connectionString);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var definition = connection.CreateCommand();
            definition.CommandText = @"INSERT INTO SYS_SCREEN_DEFINITION
                (UI_ID, TITLE, DEFINITION_JSON, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@uiId, @uiId, @json, 'SYSTEM', @now, 'SYSTEM', @now)";
            definition.Parameters.AddWithValue("@uiId", uiId);
            var historicalLegacy = JsonNode.Parse(ScreenDefinitionJson.Serialize(
                PomWorkExecutionScreenTemplate.CreateLegacyRevision1(uiId, uiId)))!.AsObject();
            RemovePostRevision1SchemaDefaults(historicalLegacy);
            var historicalLegacyJson = historicalLegacy.ToJsonString();
            historicalLegacyJson.Should().NotContain("\"hidden\":");
            historicalLegacyJson.Should().NotContain("\"valueGenerator\":");
            PomWorkExecutionScreenTemplate.CalculateManagedDefinitionFingerprint(historicalLegacy)
                .Should().Be(PomWorkExecutionScreenTemplate.LegacyRevision1GoldenSha256);
            PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(uiId, historicalLegacyJson)
                .Should().BeTrue("초기 DB에는 후대에 추가된 기본 루트 속성이 없었다");
            PomWorkExecutionScreenTemplate.IsHistoricalRevision1PlaceholderDefinition(uiId, historicalLegacyJson)
                .Should().BeTrue();
            PomWorkExecutionScreenTemplate.IsHistoricalRevision1PlaceholderDefinition(
                    uiId, PomWorkExecutionScreenTemplate.Serialize(uiId, uiId))
                .Should().BeFalse("최신 관리 화면에서 사용자가 선택한 UI ID 제목까지 초기 placeholder로 오판하면 안 된다");
            var designerExtendedLegacy = historicalLegacy.DeepClone().AsObject();
            designerExtendedLegacy["designerExtension"] = true;
            PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                    uiId, designerExtendedLegacy.ToJsonString())
                .Should().BeFalse("알 수 없는 Designer 확장 속성이 있는 legacy 화면은 자동 업그레이드하면 안 된다");
            var designerCaptionChanged = historicalLegacy.DeepClone().AsObject();
            designerCaptionChanged["searchFields"]!.AsArray()[0]!.AsObject()["caption"] = "사용자 검색 조건";
            PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                    uiId, designerCaptionChanged.ToJsonString())
                .Should().BeFalse("표시 문구만 바꾼 화면도 Designer 사용자 정의로 보존해야 한다");
            var designerQueryChanged = historicalLegacy.DeepClone().AsObject();
            designerQueryChanged["queryId"] = "POM.CustomWorkOrderList";
            PomWorkExecutionScreenTemplate.IsManagedCanonicalDefinition(
                    uiId, designerQueryChanged.ToJsonString())
                .Should().BeFalse("쿼리 계약이 다른 화면은 자동 업그레이드하면 안 된다");
            definition.Parameters.AddWithValue("@json", historicalLegacyJson);
            definition.Parameters.AddWithValue("@now", auditTime);
            await definition.ExecuteNonQueryAsync();
        }

        await using var app = builder.Build();
        NexaOneDevelopmentDatabaseInitializer.Initialize(app);

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT TITLE, DEFINITION_JSON, CREATED_AT, UPDATED_AT
                                      FROM SYS_SCREEN_DEFINITION
                                     WHERE UI_ID = @uiId";
            command.Parameters.AddWithValue("@uiId", uiId);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("MES 작업 실행");
            var upgraded = ScreenDefinitionJson.Deserialize(reader.GetString(1));
            upgraded.Should().NotBeNull();
            upgraded!.Title.Should().Be("MES 작업 실행");
            upgraded.SearchFields.Should().ContainSingle(field => field.Key == "routingScope");
            reader.GetString(2).Should().Be(auditTime);
            reader.GetString(3).Should().Be(auditTime);
        }

        try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { /* best effort */ }
    }

    private static IConfiguration Configuration(string connectionString)
        => new ConfigurationBuilder().AddInMemoryCollection(Settings(connectionString)).Build();

    /// <summary>실제 초기 DB의 revision 1 직렬화 스키마를 재현한다.</summary>
    private static void RemovePostRevision1SchemaDefaults(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in new[]
                     {
                         "purpose",
                         "readRequiredPermission",
                         "saveRequiredPermission",
                         "deleteRequiredPermission",
                     })
                obj.Remove(name);
            if (obj["hidden"]?.GetValue<bool>() == false) obj.Remove("hidden");
            if (string.Equals(obj["valueGenerator"]?.GetValue<string>(), "None", StringComparison.Ordinal))
                obj.Remove("valueGenerator");
            foreach (var child in obj.Select(property => property.Value).Where(value => value is not null).ToArray())
                RemovePostRevision1SchemaDefaults(child!);
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array.Where(value => value is not null).ToArray())
                RemovePostRevision1SchemaDefaults(child!);
        }
    }

    private static Dictionary<string, string?> Settings(string connectionString) => new()
    {
        ["Server:Modules:Enabled"] = "false",
        ["Database:Provider"] = "Sqlite",
        ["ConnectionStrings:NexaOne"] = connectionString,
        ["Jwt:SecretKey"] = JwtSecret,
        ["Jwt:Issuer"] = "nexaone-mes-facade-test",
        ["Jwt:Audience"] = "nexaone-mes-facade-test",
        ["RateLimiting:Enabled"] = "false",
    };

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static ScreenDefinition ReadScreenDefinition(SqliteConnection connection, string uiId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DEFINITION_JSON FROM SYS_SCREEN_DEFINITION WHERE UI_ID = @uiId";
        command.Parameters.AddWithValue("@uiId", uiId);
        var json = command.ExecuteScalar()?.ToString();
        return ScreenDefinitionJson.Deserialize(json ?? string.Empty)
            ?? throw new InvalidOperationException($"Screen definition '{uiId}' could not be deserialized.");
    }

    private static IEnumerable<LayoutNode> DescendantsAndSelf(LayoutNode node)
    {
        yield return node;
        var children = node switch
        {
            SectionNode section => section.Children,
            RowNode row => row.Children,
            ColumnNode column => column.Children,
            _ => null,
        };
        if (children is null) yield break;
        foreach (var child in children)
        {
            foreach (var descendant in DescendantsAndSelf(child))
                yield return descendant;
        }
    }

    private static string MintToken()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            "nexaone-mes-facade-test",
            "nexaone-mes-facade-test",
            new[] { new Claim(ClaimTypes.NameIdentifier, "facade-user") },
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class CustomScheduler : IRecurringScheduler
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task ScheduleRecurringAsync(
            string name,
            TimeSpan interval,
            Func<CancellationToken, Task> job,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleRecurringCronAsync(
            string name,
            string cron,
            Func<CancellationToken, Task> job,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UnscheduleAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "NexaOne.ServerTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class DelayedFaultWorker : BackgroundService
    {
        private readonly TaskCompletionSource _fail = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void FailAfterStartup() => _fail.TrySetResult();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _fail.Task.WaitAsync(stoppingToken);
            throw new InvalidOperationException("delayed module worker failure");
        }
    }

    private sealed class RecordingHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public int StopCount { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StopCount++;
            _stopping.Cancel();
        }
    }

    private sealed class RecordingWorker(string name, List<string> events) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add($"stop:{name}");
            return Task.CompletedTask;
        }
    }

    private sealed class ConcurrentWorkerCoordinator
    {
        private readonly int _expectedWorkers;
        private readonly TaskCompletionSource _allStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allStopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _stopped;
        private readonly List<string> _stopOrder = new();

        public ConcurrentWorkerCoordinator(int expectedWorkers) => _expectedWorkers = expectedWorkers;

        public int Started => Volatile.Read(ref _started);
        public int Stopped => Volatile.Read(ref _stopped);
        public IReadOnlyList<string> StopOrder
        {
            get { lock (_stopOrder) return _stopOrder.ToArray(); }
        }

        public Task EnterStartAsync()
        {
            if (Interlocked.Increment(ref _started) == _expectedWorkers) _allStarted.TrySetResult();
            return _allStarted.Task;
        }

        public Task EnterStopAsync(string name)
        {
            lock (_stopOrder) _stopOrder.Add(name);
            if (Interlocked.Increment(ref _stopped) == _expectedWorkers) _allStopped.TrySetResult();
            return _allStopped.Task;
        }
    }

    private sealed class CoordinatedWorker(string name, ConcurrentWorkerCoordinator coordinator) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => coordinator.EnterStartAsync();
        public Task StopAsync(CancellationToken cancellationToken) => coordinator.EnterStopAsync(name);
    }
}
