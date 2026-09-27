using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NexaOne.Application.Query;
using NexaOne.SYS.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>실제 SQL Server에서 화면 저장과 동시 채번의 잠금·보존 계약을 검증한다.</summary>
[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlIdRuleContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Save_and_parallel_claims_preserve_format_sequence_and_period()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;
        var ruleId = "RULE_" + Guid.NewGuid().ToString("N");
        var registry = FileQueryRegistry.Load("mssql",
            RepositorySource.GetDirectory("src/00.Main/NexaOne.Server/config/db/queries"));
        registry.TryGet("COM.SaveIdRule", out var definition).Should().BeTrue();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var parameters = new
        {
            ruleId, ruleName = "parallel rule", prefix = "R-{period}-", seqLength = "4",
            resetCycle = "MONTHLY", description = "contract", currentUser = "contract-test",
            utcNow = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc),
        };
        try
        {
            (await connection.ExecuteAsync(definition!.Sql, parameters with { prefix = "R-{PERIOD}-" }))
                .Should().Be(0, "기간 템플릿은 엔진과 동일한 대소문자 계약이어야 한다");
            (await connection.ExecuteAsync(definition!.Sql, parameters)).Should().Be(1);
            var clock = parameters.utcNow;
            var engine = new IdRuleEngine(database.DataSource, () => clock);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await engine.NextIdAsync(ruleId);
            })).ToArray();
            start.SetResult();
            var ids = await Task.WhenAll(claims);
            ids.Should().OnlyHaveUniqueItems();
            ids.OrderBy(id => id).Should().Equal(
                Enumerable.Range(1, 24).Select(i => $"R-202609-{i:0000}"));

            (await connection.ExecuteAsync(definition.Sql, parameters with { ruleName = "renamed" }))
                .Should().Be(1);
            (await connection.ExecuteAsync(definition.Sql, parameters with { prefix = "OTHER-{period}-" }))
                .Should().Be(0);
            (await database.ScalarAsync<int>(
                "SELECT CURRENT_SEQ FROM COM_ID_RULE WHERE RULE_ID=@ruleId", new { ruleId })).Should().Be(24);
            (await database.ScalarAsync<string>(
                "SELECT SEQ_PERIOD FROM COM_ID_RULE WHERE RULE_ID=@ruleId", new { ruleId })).Should().Be("202609");

            clock = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            (await engine.NextIdAsync(ruleId)).Should().Be("R-202610-0001");
        }
        finally
        {
            await database.ExecuteAsync("DELETE FROM COM_ID_RULE WHERE RULE_ID=@ruleId", new { ruleId });
        }
    }
}
