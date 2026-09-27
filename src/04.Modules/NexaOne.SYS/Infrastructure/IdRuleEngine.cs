using System.Data;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

/// <summary>
/// COM_ID_RULE에 대한 SYS 소유 채번 엔진입니다. UPDATE(행 잠금) → SELECT(잠긴 행 읽기)를 한 트랜잭션으로
/// 묶어 동시 채번에서도 중복 없는 시퀀스를 보장하고, 리셋 기간이 바뀌면 시퀀스를 1로 돌린다.
/// </summary>
public sealed class IdRuleEngine : IIdRuleEngine
{
    private readonly ServiceObjectProcessor _processor;
    private readonly Func<DateTime> _utcNow;

    public IdRuleEngine(EesDataSource dataSource, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<string> NextIdAsync(string ruleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        var utcNow = _utcNow();

        return await _processor.ExecuteInTransactionAsync(async (conn, txn) =>
        {
            // READ COMMITTED의 SELECT만으로는 SQL Server에서 두 호출이 같은 값을 읽을 수 있다.
            // 먼저 쓰기 잠금을 얻고 커밋까지 유지한다(SQLite도 읽기 전에 writer를 직렬화한다).
            var locked = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE COM_ID_RULE SET CURRENT_SEQ = CURRENT_SEQ WHERE RULE_ID = @ruleId",
                new { ruleId }, txn, cancellationToken: ct));
            if (locked != 1)
                throw new InvalidOperationException(
                    $"ID rule '{ruleId}' is not registered in COM_ID_RULE.");

            var rule = await conn.QueryFirstOrDefaultAsync<RuleRow>(
                new CommandDefinition("SELECT RULE_ID AS RuleId, PREFIX AS Prefix, SEQ_LENGTH AS SeqLength, " +
                "RESET_CYCLE AS ResetCycle, SEQ_PERIOD AS SeqPeriod, CURRENT_SEQ AS CurrentSeq " +
                "FROM COM_ID_RULE WHERE RULE_ID = @ruleId",
                new { ruleId }, txn, cancellationToken: ct));
            if (rule is null)
                throw new InvalidOperationException(
                    $"ID rule '{ruleId}' is not registered in COM_ID_RULE.");

            var idRule = new IdRule(rule.RuleId, rule.Prefix, rule.SeqLength, rule.ResetCycle);
            var period = idRule.PeriodKey(utcNow);
            if (rule.SeqPeriod is not null && string.CompareOrdinal(period, rule.SeqPeriod) < 0)
                throw new InvalidOperationException(
                    $"ID rule '{ruleId}' cannot move back to an earlier issuance period.");
            var stalePeriod = rule.SeqPeriod is not null && rule.SeqPeriod != period;
            if (!stalePeriod && rule.CurrentSeq == int.MaxValue)
                throw new InvalidOperationException($"ID rule '{ruleId}' sequence is exhausted.");
            var next = stalePeriod ? 1 : checked(rule.CurrentSeq + 1);
            var id = idRule.Format(next, period);

            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE COM_ID_RULE SET CURRENT_SEQ = @next, SEQ_PERIOD = @period, " +
                "UPDATED_BY = @actor, UPDATED_AT = @now WHERE RULE_ID = @ruleId",
                new { next, period, actor = "ID_RULE_ENGINE", now = utcNow, ruleId }, txn, cancellationToken: ct));

            return id;
        }, IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
    }

    private sealed class RuleRow
    {
        public string RuleId { get; set; } = string.Empty;
        public string? Prefix { get; set; }
        public int? SeqLength { get; set; }
        public string? ResetCycle { get; set; }
        public string? SeqPeriod { get; set; }
        public int CurrentSeq { get; set; }
    }
}
