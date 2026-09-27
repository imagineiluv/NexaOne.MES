using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

/// <summary>운영 읽기 전용 감사가 레거시 근거와 예외를 구분하는지 실제 MSSQL에서 검증한다.</summary>
[Trait("Category", "MssqlContract")]
[Collection(MssqlContractDatabase.CollectionName)]
public sealed class MssqlLegacyDataAuditContractTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Audit_separates_reconstructable_recipe_chains_from_ambiguous_4m_and_id_rules()
    {
        var database = await MssqlContractDatabase.TryCreateAsync(output);
        if (database is null) return;

        var suffix = Guid.NewGuid().ToString("N");
        var pending = "AUD_P_" + suffix;
        var approved1 = "AUD_A1_" + suffix;
        var selfApproved = "AUD_SELF_" + suffix;
        var noHistory = "AUD_NOH_" + suffix;
        var qmsPending = "AUD_QP_" + suffix;
        var qmsMissingActor = "AUD_QM_" + suffix;
        var qmsStateMismatch = "AUD_QX_" + suffix;
        var invalidRule = "AUD_RI_" + suffix;
        var unknownPeriod = "AUD_RU_" + suffix;
        var invalidPeriod = "AUD_RD_" + suffix;
        var validRule = "AUD_RV_" + suffix;
        var requester = "AUR_" + suffix;
        var firstApprover = "AUF_" + suffix;
        var at = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var script = await File.ReadAllTextAsync(Path.Combine(
            RepositorySource.GetDirectory("ops/sql"),
            "legacy-approval-id-rule-audit.mssql.sql"));

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        foreach (var userId in new[] { requester, firstApprover })
        {
            await connection.ExecuteAsync(
                "INSERT INTO SYS_USER (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, " +
                "CREATED_BY, UPDATED_BY) VALUES (@userId, 'audit', @hash, @email, " +
                "'ADMIN', 'audit', 'audit')",
                new { userId, hash = new string('0', 64), email = userId + "@example.test" },
                transaction);
        }

        foreach (var (id, state, first, second) in new[]
        {
            (pending, "WaitApproval", (string?)null, (string?)null),
            (approved1, "Approved1", firstApprover, (string?)null),
            (selfApproved, "Approved", firstApprover, requester),
            (noHistory, "WaitApproval", (string?)null, (string?)null),
        })
        {
            await connection.ExecuteAsync(
                "INSERT INTO RMS_RECIPE (RECIPE_ID, RECIPE_NAME, EQUIPMENT_CLASS_ID, " +
                "APPROVAL_STATE, FIRST_APPROVER_ID, SECOND_APPROVER_ID, CREATED_BY, UPDATED_BY) " +
                "VALUES (@id, 'audit', 'AUDIT', @state, @first, @second, @requester, @requester)",
                new { id, state, first, second, requester }, transaction);
        }

        await InsertHistory(pending, 1, "Draft", "WaitApproval", requester);
        await InsertHistory(approved1, 1, "Draft", "WaitApproval", requester);
        await InsertHistory(approved1, 2, "WaitApproval", "Approved1", firstApprover);
        await InsertHistory(selfApproved, 1, "Draft", "WaitApproval", requester);
        await InsertHistory(selfApproved, 2, "WaitApproval", "Approved1", firstApprover);
        await InsertHistory(selfApproved, 3, "Approved1", "Approved", requester);

        await connection.ExecuteAsync(
            "INSERT INTO QMS_4M_CHANGE (CHANGE_ID, APPROVAL_STATUS, REQUESTED_BY, " +
            "CREATED_BY, UPDATED_BY) VALUES (@id, 'Pending', 'requester', 'requester', 'requester')",
            new { id = qmsPending }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO QMS_4M_CHANGE (CHANGE_ID, APPROVAL_STATUS, REQUESTED_BY, " +
            "APPROVED_BY, APPROVED_AT, CREATED_BY, UPDATED_BY) " +
            "VALUES (@id, 'Approved', NULL, 'approver', @at, 'SYSTEM', 'SYSTEM')",
            new { id = qmsMissingActor, at }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO QMS_4M_CHANGE (CHANGE_ID, APPROVAL_STATUS, REQUESTED_BY, " +
            "APPROVED_BY, APPROVED_AT, CREATED_BY, UPDATED_BY) " +
            "VALUES (@id, 'Approved', @requester, @firstApprover, @at, @requester, @requester)",
            new { id = qmsStateMismatch, requester, firstApprover, at }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO COM_APPROVAL (APPROVAL_ID, DOC_KIND, DOC_ID, STATUS, " +
            "REQUESTED_BY, REQUESTED_AT, IDEMPOTENCY_KEY, REQUEST_HASH, CREATED_BY, UPDATED_BY) " +
            "VALUES (@id, 'FourMChange', @doc, 'Pending', @requester, @at, @key, @hash, " +
            "@requester, @requester)",
            new
            {
                id = "APR_" + suffix, doc = qmsStateMismatch, requester, at,
                key = "AUDKEY_" + suffix, hash = new string('b', 64),
            }, transaction);

        foreach (var (id, prefix, current, period) in new[]
        {
            (invalidRule, "LEG-", 5, (string?)null),
            (unknownPeriod, "LEG-{period}-", 5, (string?)null),
            (invalidPeriod, "LEG-{period}-", 5, "202613"),
            (validRule, "NEW-{period}-", 0, (string?)null),
        })
        {
            await connection.ExecuteAsync(
                "INSERT INTO COM_ID_RULE (RULE_ID, RULE_NAME, PREFIX, SEQ_LENGTH, " +
                "CURRENT_SEQ, RESET_CYCLE, SEQ_PERIOD) " +
                "VALUES (@id, 'audit', @prefix, 4, @current, 'MONTHLY', @period)",
                new { id, prefix, current, period }, transaction);
        }

        using (var result = await connection.QueryMultipleAsync(
                   new CommandDefinition(script, transaction: transaction, commandTimeout: 60)))
        {
            var recipes = (await result.ReadAsync<ApprovalFinding>()).ToDictionary(x => x.DocId);
            var fourM = (await result.ReadAsync<ApprovalFinding>()).ToDictionary(x => x.DocId);
            var rules = (await result.ReadAsync<RuleFinding>()).ToDictionary(x => x.RuleId);

            recipes[pending].Finding.Should().Be("SOURCE_CHAIN_COMPLETE");
            recipes[approved1].Finding.Should().Be("SOURCE_CHAIN_COMPLETE");
            recipes[selfApproved].Finding.Should().Be("SELF_APPROVAL_CONFLICT");
            recipes[noHistory].Finding.Should().Be("SOURCE_CHAIN_INCOMPLETE");
            fourM[qmsPending].Finding.Should().Be("REQUEST_TIME_UNPROVEN");
            fourM[qmsMissingActor].Finding.Should().Be("REQUESTER_MISSING");
            fourM[qmsStateMismatch].Finding.Should().Be("STATE_MISMATCH");
            rules[invalidRule].Finding.Should().Be("PERIOD_PLACEHOLDER_MISSING");
            rules[unknownPeriod].Finding.Should().Be("ISSUED_PERIOD_UNKNOWN");
            rules[invalidPeriod].Finding.Should().Be("ISSUED_PERIOD_INVALID");
            rules.Should().NotContainKey(validRule);
        }

        // The report is SELECT-only: it must not create shared approval records or change source data.
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM COM_APPROVAL WHERE DOC_ID IN (@pending, @approved1, @qmsPending)",
            new { pending, approved1, qmsPending }, transaction)).Should().Be(0);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT CURRENT_SEQ FROM COM_ID_RULE WHERE RULE_ID = @id",
            new { id = unknownPeriod }, transaction)).Should().Be(5);

        await transaction.RollbackAsync();

        async Task InsertHistory(string recipeId, int step, string from, string to, string actor)
        {
            await connection.ExecuteAsync(
                "INSERT INTO RMS_RECIPE_APPROVAL_HISTORY (HISTORY_ID, IDEMPOTENCY_KEY, " +
                "REQUEST_HASH, RECIPE_ID, FROM_STATE, TO_STATE, CHANGED_BY, CHANGED_AT) " +
                "VALUES (@historyId, @key, @hash, @recipeId, @from, @to, @actor, @changedAt)",
                new
                {
                    historyId = $"AH_{Guid.NewGuid():N}",
                    key = $"AK_{Guid.NewGuid():N}",
                    hash = new string('a', 64),
                    recipeId, from, to, actor,
                    changedAt = at.AddMinutes(step),
                }, transaction);
        }
    }

    private sealed class ApprovalFinding
    {
        public string DocId { get; set; } = string.Empty;
        public string Finding { get; set; } = string.Empty;
    }

    private sealed class RuleFinding
    {
        public string RuleId { get; set; } = string.Empty;
        public string Finding { get; set; } = string.Empty;
    }
}
