using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>SYS-owned COM_ID_RULE engine: atomic sequence claims, formatting, and period resets.</summary>
public sealed class SysIdRuleEnginePersistenceTests
    : IClassFixture<SysIdRuleEnginePersistenceTests.IdRuleFactory>
{
    private const string Secret = "sys-idrule-persistence-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-sys-idrule-persistence-test";
    private readonly IdRuleFactory _factory;

    public SysIdRuleEnginePersistenceTests(IdRuleFactory factory) => _factory = factory;

    public sealed class IdRuleFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-idrule-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Next_id_increments_atomically_and_never_repeats()
    {
        _ = _factory.CreateClient();
        InsertRule("WO", "작업지시 채번", "WO-", 5, 0, null);
        var engine = Engine();

        var issued = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => engine.NextIdAsync("WO")));

        issued.Should().OnlyHaveUniqueItems("concurrent claims must never return the same id");
        issued.OrderBy(x => x).Should().Equal(
            Enumerable.Range(1, 8).Select(i => $"WO-{i:00000}"));
        Scalar<int>("SELECT CURRENT_SEQ FROM COM_ID_RULE WHERE RULE_ID='WO'").Should().Be(8);
    }

    [Fact]
    public async Task Period_boundary_restarts_the_sequence_and_stamps_seq_period()
    {
        _ = _factory.CreateClient();
        InsertRule("REQ", "요청 채번", "REQ-", 3, 4, "Monthly");
        var clock = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var engine = new IdRuleEngine(DataSource(), () => clock);

        (await engine.NextIdAsync("REQ")).Should().Be("REQ-005",
            "첫 발급은 현재 시퀀스 4의 다음 값이다");
        clock = new DateTime(2026, 10, 1, 0, 30, 0, DateTimeKind.Utc);
        (await engine.NextIdAsync("REQ")).Should().Be("REQ-001",
            "월 경계가 바뀌면 시퀀스를 1로 되돌린다(일별/월별 규칙은 PREFIX가 날짜를 담아야 한다는 전제 유지)");
        Scalar<string>("SELECT SEQ_PERIOD FROM COM_ID_RULE WHERE RULE_ID='REQ'")
            .Should().Be("202610");
    }

    [Fact]
    public async Task Unknown_rule_fails_fast_without_touching_storage()
    {
        _ = _factory.CreateClient();
        var act = () => Engine().NextIdAsync("MISSING");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MISSING*");
    }

    private IdRuleEngine Engine() => new(DataSource());

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString,
    };

    private void InsertRule(
        string ruleId, string name, string prefix, int seqLength, int currentSeq, string? resetCycle)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO COM_ID_RULE (RULE_ID, RULE_NAME, PREFIX, SEQ_LENGTH, CURRENT_SEQ, RESET_CYCLE) " +
            "VALUES (@id, @name, @prefix, @len, @seq, @cycle)";
        command.Parameters.AddWithValue("@id", ruleId);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@prefix", prefix);
        command.Parameters.AddWithValue("@len", seqLength);
        command.Parameters.AddWithValue("@seq", currentSeq);
        command.Parameters.AddWithValue("@cycle", (object?)resetCycle ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
