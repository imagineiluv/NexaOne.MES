using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.WorkOrders;
using NexaOne.POM.Domain;
using NexaOne.POM.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.ServiceContracts.Qms;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>POM Lot Mixing 원자화(DATA-3) SQLite 통합검증 — 호스트가 부트한 DB(dev 시드: PLANT01/EQ01 Active)에
/// 실제 POM 리포지토리/서비스를 직접 구성해 (1)성공 Mixing이 투입 소비·혼합관계·이력·출력 Lot을 모두 커밋하고
/// (2)배치 중 한 문장(혼합관계 PK 충돌)이 실패하면 전체가 롤백돼 투입 Lot이 소비되지 않음(부분 커밋 불가)을 검증한다.
/// 리팩토링 전에는 투입 소비 후 후속 실패 시 부분 커밋이 가능했다 — 이 테스트가 그 회귀를 잠근다.</summary>
public sealed class PomMixingAtomicityTests : IClassFixture<PomMixingAtomicityTests.MixFactory>
{
    private readonly MixFactory _factory;
    public PomMixingAtomicityTests(MixFactory factory) => _factory = factory;

    public sealed class MixFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-mixing-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "pom-mixing-e2e-jwt-secret-key-32bytes+!!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-mixing-test");
            builder.UseSetting("Jwt:Audience", "nexaone-mixing-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    /// <summary>Mixing does not resolve work orders; any call exposes an unexpected dependency.</summary>
    private sealed class UnusedWorkOrders : IPomWorkOrderRepository
    {
        public Task<PomWorkOrder?> GetByIdAsync(string workOrderId, CancellationToken ct = default)
            => throw new InvalidOperationException("Mixing must not resolve work orders.");
        public Task<IReadOnlyList<PomWorkOrder>> GetByProductionOrderAsync(
            string productionOrderId, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task<bool> ExecutionExistsAsync(string idempotencyKey, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task<PomWorkOrderExecution?> GetExecutionByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
            => throw new InvalidOperationException();
        public Task AddAsync(PomWorkOrder workOrder, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<bool> UpdateAsync(PomWorkOrder workOrder, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<bool> UpdateWithExecutionAsync(PomWorkOrder workOrder, PomWorkOrderExecution execution, CancellationToken ct = default)
            => throw new InvalidOperationException();
    }

    private LotTrackingService BuildService()
    {
        _ = _factory.CreateClient(); // 스키마 + dev 시드(PLANT01/EQ01 Active)
        var ds = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString,
        };
        var config = new ConfigurationBuilder().Build(); // outbox off
        return new LotTrackingService(
            new LotRepository(ds, config),
            new LotHistoryRepository(ds, new SqliteEesDbCapability()),
            new LotMixingRelationRepository(ds),
            new UnusedWorkOrders(),
            new TrackingMasterGateway(ds),
            new ProductionQualityGateService(
                new ProductionQualityGateEvidenceRepository(ds)));
    }

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        _ = _factory.CreateClient(); // 스키마 부트스트랩 보장(시드가 서비스 구성보다 먼저 실행될 수 있음)
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private void SeedQueuedLot(string lotId, decimal qty)
        => Exec(@"INSERT INTO POM_LOT
            (LOT_ID, PLANT_ID, PRODUCT_ID, QTY, DEFECT_QTY, LOT_STATE, PROCESS_STATE, ROUTE_STEPS, CURRENT_STEP, IS_HOLD, CREATED_BY, CREATED_AT)
            VALUES (@id, 'PLANT01', 'ITEM01', @qty, 0, 'Queued', 'Idle', 'MIX', 0, 'N', 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", lotId);
            cmd.Parameters.AddWithValue("@qty", qty);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        });

    private T Scalar<T>(string sql, params (string Key, object Value)[] ps)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static MixingTrackCommand Command(string outputLotId, params MixingInput[] inputs)
        => new(PlantId: "PLANT01", OutputLotId: outputLotId, ProductId: "ITEM01", EquipmentId: "EQ01",
               OutputRouteSteps: new[] { "MIX" }, Inputs: inputs, User: "mix-tester");

    [Fact]
    public async Task Successful_mixing_commits_inputs_output_relations_and_histories()
    {
        var inA = $"MIN_{Suffix()}";
        var inB = $"MIN_{Suffix()}";
        var output = $"MOUT_{Suffix()}";
        SeedQueuedLot(inA, 6m);
        SeedQueuedLot(inB, 4m);

        var result = await BuildService().MixingTrackInOutAsync(Command(output, new(inA, 6m), new(inB, 4m)));

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Description : "");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@id", ("@id", inA)).Should().Be("Consumed");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@id", ("@id", inB)).Should().Be("Consumed");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@id", ("@id", output)).Should().Be("Completed",
            "TrackIn->TrackOut 연속 수행으로 완료된 출력 Lot이 커밋돼야 한다");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_MIXING_RELATION WHERE OUTPUT_LOT_ID=@id", ("@id", output)).Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_HISTORY WHERE LOT_ID IN (@a,@b,@o)",
            ("@a", inA), ("@b", inB), ("@o", output)).Should().BeGreaterThanOrEqualTo(5,
            "Consume 2 + TrackIn + TrackOut + Finish 이력이 같은 트랜잭션으로 커밋돼야 한다");
    }

    [Fact]
    public async Task Failed_statement_rolls_back_entire_mixing_no_partial_commit()
    {
        var inA = $"MIN_{Suffix()}";
        var inB = $"MIN_{Suffix()}";
        var output = $"MOUT_{Suffix()}";
        SeedQueuedLot(inA, 5m);
        SeedQueuedLot(inB, 5m);
        // 두 번째 투입의 혼합관계 INSERT가 PK(PLANT,OUTPUT,INPUT) 충돌로 실패하도록 선점 행을 심는다.
        var trigger = $"TR_TEST_MIX_FAIL_{Suffix()}";
        Exec($"CREATE TRIGGER {trigger} BEFORE INSERT ON POM_LOT_MIXING_RELATION " +
             $"WHEN NEW.OUTPUT_LOT_ID = '{output}' AND NEW.INPUT_LOT_ID = '{inB}' " +
             "BEGIN SELECT RAISE(ABORT, 'forced relation failure'); END", _ => { });

        var act = () => BuildService().MixingTrackInOutAsync(Command(output, new(inA, 5m), new(inB, 5m)));
        await act.Should().ThrowAsync<Exception>("배치 중 혼합관계 PK 충돌은 예외로 표면화돼야 한다");

        // 원자성 — 어떤 문장도 커밋되지 않아야 한다(리팩토링 전에는 inA 소비가 부분 커밋됐다).
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@id", ("@id", inA)).Should().Be("Queued",
            "롤백 후 첫 번째 투입 Lot은 소비되지 않아야 한다(부분 커밋 불가)");
        Scalar<string>("SELECT LOT_STATE FROM POM_LOT WHERE LOT_ID=@id", ("@id", inB)).Should().Be("Queued");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT WHERE LOT_ID=@id", ("@id", output)).Should().Be(0,
            "출력 Lot도 생성되지 않아야 한다");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_MIXING_RELATION WHERE OUTPUT_LOT_ID=@id AND INPUT_LOT_ID=@in",
            ("@id", output), ("@in", inA)).Should().Be(0, "첫 번째 투입의 혼합관계도 롤백돼야 한다");
        Scalar<long>("SELECT COUNT(*) FROM POM_LOT_HISTORY WHERE LOT_ID IN (@a,@b)",
            ("@a", inA), ("@b", inB)).Should().Be(0, "이력도 롤백돼야 한다");
    }
}
