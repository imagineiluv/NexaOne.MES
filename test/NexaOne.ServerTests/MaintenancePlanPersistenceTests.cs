using System.Globalization;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.EMS.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Sqlite;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// 보전계획 명령의 실제 SQLite 저장 경계. 계획 상태, 인증 행동 이력과 선택적 outbox가
/// 한 트랜잭션으로 움직이고 재시도/경합에서도 중복 증거가 생기지 않는지 검증한다.
/// </summary>
public sealed class MaintenancePlanPersistenceTests : IDisposable
{
    private static readonly DateTime Scheduled =
        new(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"nexaone-maintenance-plan-{Guid.NewGuid():N}.db");
    private readonly EesDataSource _dataSource;

    public MaintenancePlanPersistenceTests()
    {
        var connectionString =
            $"Data Source={_databasePath};Foreign Keys=False;Default Timeout=10";
        _dataSource = new EesDataSource
        {
            Provider = new SqliteProvider(),
            ConnectionString = connectionString,
        };
        CreateSchema(connectionString);
    }

    [Fact]
    public async Task Lifecycle_records_authenticated_actions_and_replays_only_exact_commands()
    {
        var service = Service(outboxEnabled: false);
        var create = Command("plan:create", "planner-1");

        var created = await service.CreatePlanAsync(
            "PLAN-01", "Monthly cleaner PM", "EQ-01", "PM", "Monthly",
            Scheduled, 2.5m, "tech-1", create);
        var replay = await Service(false).CreatePlanAsync(
            "PLAN-01", "Monthly cleaner PM", "EQ-01", "PM", "Monthly",
            Scheduled, 2.5m, "tech-1", create);
        var changedReplay = await Service(false).CreatePlanAsync(
            "PLAN-01", "Changed monthly PM", "EQ-01", "PM", "Monthly",
            Scheduled, 2.5m, "tech-1", create);

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue(replay.IsFailure ? replay.Error.Description : string.Empty);
        changedReplay.IsFailure.Should().BeTrue();
        changedReplay.Error.Code.Should().Be("EMS.MaintenancePlan.IdempotencyConflict");

        var start = Command("plan:start", "maintenance-login");
        (await Service(false).StartPlanAsync("PLAN-01", start)).IsSuccess.Should().BeTrue();
        (await Service(false).StartPlanAsync("PLAN-01", start)).IsSuccess.Should().BeTrue(
            "an exact retry must not attempt the InProgress transition again");
        var changedStartReplay = await Service(false).StartPlanAsync(
            "PLAN-01", Command("plan:start", "different-maintainer"));
        changedStartReplay.IsFailure.Should().BeTrue();
        changedStartReplay.Error.Code.Should().Be("EMS.MaintenancePlan.IdempotencyConflict");
        (await Service(false).CompletePlanAsync(
            "PLAN-01", Command("plan:complete", "maintenance-supervisor")))
            .IsSuccess.Should().BeTrue();

        var repository = Repository(false);
        var stored = await repository.GetByIdAsync("PLAN-01");
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(MaintenancePlanStatus.Completed);
        stored.CreatedBy.Should().Be("planner-1");
        stored.UpdatedBy.Should().Be("maintenance-supervisor");

        var createAction = await repository.GetActionByIdempotencyKeyAsync("plan:create");
        createAction.Should().NotBeNull();
        createAction!.ActionId.Should().NotBeNullOrWhiteSpace();
        createAction.PlanId.Should().Be("PLAN-01");
        createAction.ActionType.Should().Be("Create");
        createAction.ActorId.Should().Be("planner-1");
        createAction.ClientChannel.Should().Be("POP");
        createAction.DeviceId.Should().Be("PANEL-01");
        createAction.CorrelationId.Should().Be("corr-plan");
        createAction.FromStatus.Should().BeNull();
        createAction.ToStatus.Should().Be("Planned");

        var startAction = await repository.GetActionByIdempotencyKeyAsync("plan:start");
        startAction.Should().NotBeNull();
        startAction!.FromStatus.Should().Be("Planned");
        startAction.ToStatus.Should().Be("InProgress");

        var cancelPlan = await Service(false).CreatePlanAsync(
            "PLAN-02", "Cancelled PM", "EQ-02", "PM", "Weekly",
            Scheduled.AddDays(1), 1m, "tech-2", Command("plan-02:create", "planner-2"));
        cancelPlan.IsSuccess.Should().BeTrue();
        (await Service(false).CancelPlanAsync(
            "PLAN-02", Command("plan-02:cancel", "maintenance-login")))
            .IsSuccess.Should().BeTrue();
        (await repository.GetByIdAsync("PLAN-02"))!.Status
            .Should().Be(MaintenancePlanStatus.Cancelled);
    }

    [Fact]
    public async Task Competing_status_updates_allow_one_action_and_reject_the_lost_guard()
    {
        (await Service(false).CreatePlanAsync(
            "PLAN-RACE", "Race PM", "EQ-RACE", "PM", "Daily",
            Scheduled, 1m, "tech-race", Command("race:create", "planner")))
            .IsSuccess.Should().BeTrue();

        var firstRepository = Repository(false);
        var secondRepository = Repository(false);
        var firstPlan = (await firstRepository.GetByIdAsync("PLAN-RACE"))!;
        var secondPlan = (await secondRepository.GetByIdAsync("PLAN-RACE"))!;
        firstPlan.Start().IsSuccess.Should().BeTrue();
        secondPlan.Start().IsSuccess.Should().BeTrue();
        var firstAction = Action(firstPlan, "race:start:a", "maint-a");
        var secondAction = Action(secondPlan, "race:start:b", "maint-b");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[]
        {
            Task.Run(async () =>
            {
                await start.Task;
                return await firstRepository.UpdateWithActionAsync(firstPlan, firstAction);
            }),
            Task.Run(async () =>
            {
                await start.Task;
                return await secondRepository.UpdateWithActionAsync(secondPlan, secondAction);
            }),
        };
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(x => x).Should().Be(1);
        (await Repository(false).GetByIdAsync("PLAN-RACE"))!.Status
            .Should().Be(MaintenancePlanStatus.InProgress);
        var persistedActions = new[]
        {
            await Repository(false).GetActionByIdempotencyKeyAsync("race:start:a"),
            await Repository(false).GetActionByIdempotencyKeyAsync("race:start:b"),
        };
        persistedActions.Count(x => x is not null).Should().Be(1);
    }

    [Fact]
    public async Task Outbox_failure_rolls_back_plan_and_action_then_same_command_can_retry()
    {
        (await Service(true).CreatePlanAsync(
            "PLAN-OUTBOX", "Outbox PM", "EQ-OUTBOX", "PM", "Weekly",
            Scheduled, 1.5m, "tech-outbox", Command("outbox:create", "planner")))
            .IsSuccess.Should().BeTrue();
        Execute("""
            CREATE TRIGGER FAIL_PLAN_OUTBOX
            BEFORE INSERT ON EES_OUTBOX
            BEGIN
                SELECT RAISE(ABORT, 'forced outbox failure');
            END;
            """);

        var command = Command("outbox:start", "maintenance-login");
        Func<Task> failedStart = async () =>
            await Service(true).StartPlanAsync("PLAN-OUTBOX", command);
        await failedStart.Should().ThrowAsync<SqliteException>();

        var afterFailure = await Repository(true).GetByIdAsync("PLAN-OUTBOX");
        afterFailure!.Status.Should().Be(MaintenancePlanStatus.Planned);
        (await Repository(true).GetActionByIdempotencyKeyAsync("outbox:start"))
            .Should().BeNull();
        Scalar<long>("SELECT COUNT(*) FROM EES_OUTBOX").Should().Be(0);

        Execute("DROP TRIGGER FAIL_PLAN_OUTBOX");
        var retry = await Service(true).StartPlanAsync("PLAN-OUTBOX", command);

        retry.IsSuccess.Should().BeTrue(retry.IsFailure ? retry.Error.Description : string.Empty);
        (await Repository(true).GetByIdAsync("PLAN-OUTBOX"))!.Status
            .Should().Be(MaintenancePlanStatus.InProgress);
        (await Repository(true).GetActionByIdempotencyKeyAsync("outbox:start"))
            .Should().NotBeNull();
        Scalar<long>("SELECT COUNT(*) FROM EES_OUTBOX").Should().Be(1);
        Scalar<string>("SELECT CREATED_BY FROM EES_OUTBOX")
            .Should().Be("maintenance-login");
        Scalar<string>("SELECT EVENT_TYPE FROM EES_OUTBOX")
            .Should().Be("MaintenancePlanStarted");
    }

    [Fact]
    public async Task Legacy_part_creation_persists_the_authenticated_actor()
    {
        var created = await Service(false).CreatePartAsync(
            "PART-01", "Bearing", "BR-01", "Drive bearing", "EA",
            10m, 2m, 30m, "RACK-A", null, "logged-maintainer");

        created.IsSuccess.Should().BeTrue(created.IsFailure ? created.Error.Description : string.Empty);
        var stored = await new SparePartRepository(_dataSource).GetByIdAsync("PART-01");
        stored.Should().NotBeNull();
        stored!.CreatedBy.Should().Be("logged-maintainer");
        stored.UpdatedBy.Should().Be("logged-maintainer");
    }

    private MaintenancePlanService Service(bool outboxEnabled) => new(
        Repository(outboxEnabled), new SparePartRepository(_dataSource));

    private MaintenancePlanRepository Repository(bool outboxEnabled) => new(
        _dataSource,
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Events:Outbox:Enabled"] = outboxEnabled ? "true" : "false",
            }).Build());

    private static MaintenanceCommandContext Command(string key, string actor) =>
        MaintenanceCommandContext.Create(
            actor, key, "POP", "PANEL-01", "corr-plan").Value;

    private static MaintenancePlanAction Action(
        MaintenancePlan plan,
        string key,
        string actor) => new(
        Guid.NewGuid().ToString("N"), plan.Id, "Start", "Planned", "InProgress",
        actor, key, DateTime.UtcNow, "Manual", "POP", "PANEL-01", "corr-race");

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(_dataSource.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection(_dataSource.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(
            command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static void CreateSchema(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE EMS_MAINTENANCE_PLAN (
                PLAN_ID TEXT NOT NULL PRIMARY KEY,
                PLAN_NAME TEXT NOT NULL,
                EQUIPMENT_ID TEXT NOT NULL,
                PLAN_TYPE TEXT NOT NULL,
                CYCLE_TYPE TEXT NOT NULL,
                SCHEDULED_DATE TEXT NOT NULL,
                ESTIMATED_DURATION_HOURS NUMERIC NOT NULL,
                ASSIGNEE_ID TEXT NOT NULL,
                STATUS TEXT NOT NULL,
                CREATED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL,
                UPDATED_BY TEXT NOT NULL,
                UPDATED_AT TEXT NOT NULL
            );

            CREATE TABLE EMS_MAINTENANCE_ACTION_HISTORY (
                ACTION_ID TEXT NOT NULL PRIMARY KEY,
                WO_ID TEXT NULL,
                MAINTENANCE_PLAN_ID TEXT NULL,
                EQUIPMENT_ID TEXT NOT NULL,
                MAINTENANCE_TYPE TEXT NOT NULL,
                ACTION_TYPE TEXT NOT NULL,
                RESULT_STATUS TEXT NOT NULL,
                ACTOR_ID TEXT NOT NULL,
                ASSIGNEE_ID TEXT NULL,
                SOURCE TEXT NOT NULL,
                CLIENT_CHANNEL TEXT NOT NULL,
                DEVICE_ID TEXT NULL,
                FAILURE_CODE_ID TEXT NULL,
                REMARK TEXT NULL,
                ACTION_AT TEXT NOT NULL,
                IDEMPOTENCY_KEY TEXT NULL,
                FROM_STATUS TEXT NULL,
                TO_STATUS TEXT NULL,
                CORRELATION_ID TEXT NULL,
                CREATED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL
            );
            CREATE UNIQUE INDEX UX_EMS_MAINTENANCE_ACTION_IDEMPOTENCY
                ON EMS_MAINTENANCE_ACTION_HISTORY (IDEMPOTENCY_KEY)
                WHERE IDEMPOTENCY_KEY IS NOT NULL;

            CREATE TABLE EES_OUTBOX (
                ID INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                EVENT_TYPE TEXT NOT NULL,
                MODULE TEXT NOT NULL,
                AGGREGATE_ID TEXT NOT NULL,
                PAYLOAD TEXT NOT NULL,
                OCCURRED_AT TEXT NOT NULL,
                PUBLISHED_AT TEXT NULL,
                ATTEMPTS INTEGER NOT NULL,
                CREATED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL,
                UPDATED_BY TEXT NOT NULL,
                UPDATED_AT TEXT NOT NULL
            );

            CREATE TABLE EMS_SPARE_PART (
                PART_ID TEXT NOT NULL PRIMARY KEY,
                PART_NAME TEXT NOT NULL,
                PART_NUMBER TEXT NOT NULL,
                DESCRIPTION TEXT NULL,
                UNIT_OF_MEASURE TEXT NOT NULL,
                CURRENT_STOCK NUMERIC NOT NULL,
                MIN_STOCK NUMERIC NOT NULL,
                MAX_STOCK NUMERIC NOT NULL,
                LOCATION TEXT NOT NULL,
                EQUIPMENT_CLASS_ID TEXT NULL,
                CREATED_BY TEXT NOT NULL,
                CREATED_AT TEXT NOT NULL,
                UPDATED_BY TEXT NOT NULL,
                UPDATED_AT TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_databasePath)) File.Delete(_databasePath);
        }
        catch
        {
            // best effort temporary database cleanup
        }
    }
}
