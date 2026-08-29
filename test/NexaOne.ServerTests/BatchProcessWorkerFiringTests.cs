using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaOne.Server.Gateway;
using NexaFramework.Scheduling;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>Option C 라이브 검증 — 배치 재조정 워커가 DB의 Interval 정의를 실제 Quartz 스케줄러의 잡으로
/// 등록하고, 그 잡이 발화해 BatchProcessRunner 단일 경로로 실행·이력 기록까지 도달하는지 실 부트 DB로 검증한다.
/// (단위 BuildDesired가 결정 로직을, 이 테스트가 등록→발화→실행→이력 전 경로를 커버한다.)</summary>
public sealed class BatchProcessWorkerFiringTests : IClassFixture<BatchProcessWorkerFiringTests.BatchFactory>
{
    private readonly BatchFactory _factory;
    public BatchProcessWorkerFiringTests(BatchFactory factory) => _factory = factory;

    public sealed class BatchFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-batchfire-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // 모듈 OFF — 스케줄러는 테스트에서 직접 QuartzScheduler를 생성해 워커에 주입한다(경량 부트).
            // 게이트웨이(IRuleDispatcher)·BatchProcessRunner는 모듈과 무관하게 등록되므로 실행 경로가 성립한다.
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "batch-fire-e2e-jwt-secret-key-32bytes+!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-batchfire-test");
            builder.UseSetting("Jwt:Audience", "nexaone-batchfire-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    [Fact]
    public async Task Worker_registers_interval_batch_as_quartz_job_and_it_fires_recording_history()
    {
        _ = _factory.CreateClient();   // 스키마 부트스트랩(SqliteSchemaInitializer가 전 마이그레이션 적용)

        // 1초 간격 배치 시드 — 무해 룰(SYS.PurgeOldAppLogs, retention 99999일 → 삭제 0행)로 성공 기록, SAVE_HISTORY=1.
        using (var conn = new SqliteConnection(_factory.ConnString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO SYS_BATCH_PROCESS
                (BATCH_ID, BATCH_NAME, BATCH_TYPE, BATCH_RULE, BATCH_OPTIONS, BATCH_INPUTDATA,
                 AUTO_TRANSACTION, SAVE_HISTORY, VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES ('FIRE-TEST', '발화 검증', 'Interval', 'SYS.PurgeOldAppLogs', '1', '{""retentionDays"":99999}',
                        1, 1, 'Valid', 'TEST', @now, 'TEST', @now)";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        var scheduler = new QuartzScheduler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:Sys:BatchProcess:Enabled"] = "true",
            ["Worker:Sys:BatchProcess:PollSeconds"] = "5",   // 재조정 주기(최소 5s) — 발화는 잡 간격(1s)이 결정
        }).Build();
        var worker = new BatchProcessWorker(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            scheduler, config,
            _factory.Services.GetRequiredService<ILogger<BatchProcessWorker>>());

        await worker.StartAsync(CancellationToken.None);   // ExecuteAsync 시작 → 즉시 재조정 → batch:FIRE-TEST 등록(1s)
        try
        {
            // Quartz StartNow(즉시) + 1s 반복 — 최대 ~10s 폴링 안에 이력 행이 쌓여야 한다.
            long count = 0;
            for (var i = 0; i < 40 && count < 1; i++)
            {
                await Task.Delay(250);
                count = Scalar("SELECT COUNT(*) FROM SYS_BATCH_PROCESS_HISTORY WHERE BATCH_ID='FIRE-TEST'");
            }

            count.Should().BeGreaterThanOrEqualTo(1,
                "워커가 배치를 Quartz 잡으로 등록하고 발화해 이력을 남겨야 한다(등록→발화→실행→이력 전 경로)");
            Scalar("SELECT COUNT(*) FROM SYS_BATCH_PROCESS_HISTORY WHERE BATCH_ID='FIRE-TEST' AND SUCCESS=1")
                .Should().BeGreaterThanOrEqualTo(1, "무해 룰 실행은 성공(SUCCESS=1)으로 기록돼야 한다");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private long Scalar(string sql)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(long), CultureInfo.InvariantCulture);
    }
}
