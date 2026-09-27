using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class QmsFourMApprovalPersistenceTests
    : IClassFixture<QmsFourMApprovalPersistenceTests.FourMFactory>
{
    private readonly FourMFactory _factory;

    public QmsFourMApprovalPersistenceTests(FourMFactory factory) => _factory = factory;

    public sealed class FourMFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-fourm-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "qms-fourm-persistence-jwt-secret-32bytes+!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-qms-fourm-test");
            builder.UseSetting("Jwt:Audience", "nexaone-qms-fourm-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Submit_and_decide_update_QMS_and_SYS_together_with_idempotent_replay()
    {
        _ = _factory.CreateClient();
        var source = DataSource();
        var repository = new FourMChangeRepository(source);
        var approvals = new ApprovalProcess(source);
        var service = new FourMChangeService(repository, approvals);
        var id = $"4M-{Guid.NewGuid():N}";
        var request = new SubmitFourMChangeDto(
            $"submit-{id}", id, "Machine", DateTime.UtcNow, "Replace the spindle", "CHG-001");

        var submitted = await service.SubmitAsync(request, "requester");
        submitted.IsSuccess.Should().BeTrue();
        submitted.Value.ApprovalStatus.Should().Be("Pending");
        (await repository.GetByIdAsync(id))!.ApprovalStatus.Should().Be("Pending");
        (await approvals.GetCurrentAsync("FourMChange", id))!.Status.Should().Be("Pending");

        var replay = await service.SubmitAsync(request, "requester");
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ApprovalId.Should().Be(submitted.Value.ApprovalId);
        (await approvals.GetHistoryAsync("FourMChange", id)).Should().ContainSingle();

        var changedPayload = await service.SubmitAsync(request with { Description = "Different" }, "requester");
        changedPayload.IsFailure.Should().BeTrue();

        var selfDecision = await service.DecideAsync(
            id, new DecideFourMChangeDto($"self-{id}", true), "requester");
        selfDecision.IsFailure.Should().BeTrue();
        (await repository.GetByIdAsync(id))!.ApprovalStatus.Should().Be("Pending");

        var decision = new DecideFourMChangeDto($"decide-{id}", true, "Verified");
        var approved = await service.DecideAsync(id, decision, "approver");
        approved.IsSuccess.Should().BeTrue();
        approved.Value.ApprovalStatus.Should().Be("Approved");
        approved.Value.DecidedBy.Should().Be("approver");
        (await repository.GetByIdAsync(id))!.ApprovalStatus.Should().Be("Approved");
        (await approvals.GetCurrentAsync("FourMChange", id))!.Status.Should().Be("Approved");

        var decisionReplay = await service.DecideAsync(id, decision, "approver");
        decisionReplay.IsSuccess.Should().BeTrue();
        (await approvals.GetHistoryAsync("FourMChange", id)).Select(x => x.ToStatus)
            .Should().Equal("Pending", "Approved");
    }

    [Fact]
    public async Task Document_failure_rolls_back_the_shared_approval_transaction()
    {
        _ = _factory.CreateClient();
        var source = DataSource();
        var approvals = new ApprovalProcess(source);
        var id = $"4M-{Guid.NewGuid():N}";

        var operation = () => approvals.SubmitWithDocumentAsync(
            new ApprovalRequest("FourMChange", id), "requester", $"submit-{id}", "hash",
            async (transaction, ct) =>
            {
                await transaction.Connection!.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO QMS_4M_CHANGE (CHANGE_ID, CHANGE_TYPE, APPROVAL_STATUS, " +
                    "CREATED_BY, UPDATED_BY) VALUES (@id, 'Machine', 'Pending', 'requester', 'requester')",
                    new { id }, transaction, cancellationToken: ct));
                throw new InvalidOperationException("Forced document failure");
            });

        await operation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Forced document failure");
        (await approvals.GetCurrentAsync("FourMChange", id)).Should().BeNull();
        (await new FourMChangeRepository(source).GetByIdAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task Rejection_requires_a_reason_and_records_it_in_shared_history()
    {
        _ = _factory.CreateClient();
        var source = DataSource();
        var approvals = new ApprovalProcess(source);
        var repository = new FourMChangeRepository(source);
        var service = new FourMChangeService(repository, approvals);
        var id = $"4M-{Guid.NewGuid():N}";
        var submitted = await service.SubmitAsync(new SubmitFourMChangeDto(
            $"submit-{id}", id, "Method", DateTime.UtcNow, "Change work instruction"),
            "requester");
        submitted.IsSuccess.Should().BeTrue();

        var missingReason = await service.DecideAsync(
            id, new DecideFourMChangeDto($"reject-empty-{id}", false), "reviewer");
        missingReason.IsFailure.Should().BeTrue();
        (await approvals.GetCurrentAsync("FourMChange", id))!.Status.Should().Be("Pending");

        var rejected = await service.DecideAsync(
            id, new DecideFourMChangeDto($"reject-{id}", false, "Need validation"), "reviewer");
        rejected.IsSuccess.Should().BeTrue();
        rejected.Value.ApprovalStatus.Should().Be("Rejected");
        (await repository.GetByIdAsync(id))!.ApprovalStatus.Should().Be("Rejected");
        (await approvals.GetHistoryAsync("FourMChange", id)).Last().Reason
            .Should().Be("Need validation");
    }

    [Fact]
    public async Task Decision_document_failure_leaves_both_states_pending()
    {
        _ = _factory.CreateClient();
        var source = DataSource();
        var approvals = new ApprovalProcess(source);
        var repository = new FourMChangeRepository(source);
        var service = new FourMChangeService(repository, approvals);
        var id = $"4M-{Guid.NewGuid():N}";
        var submitted = await service.SubmitAsync(new SubmitFourMChangeDto(
            $"submit-{id}", id, "Material", DateTime.UtcNow, "Supplier substitution"),
            "requester");
        submitted.IsSuccess.Should().BeTrue();

        var operation = () => approvals.DecideWithDocumentAsync(
            new ApprovalDecision(submitted.Value.ApprovalId!, true),
            "approver", $"decide-{id}", "hash",
            async (transaction, ct) =>
            {
                (await repository.TryDecideAsync(
                    id, "Approved", "approver", DateTime.UtcNow, transaction, ct))
                    .Should().BeTrue();
                throw new InvalidOperationException("Forced decision failure");
            });

        await operation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Forced decision failure");
        (await repository.GetByIdAsync(id))!.ApprovalStatus.Should().Be("Pending");
        (await approvals.GetCurrentAsync("FourMChange", id))!.Status.Should().Be("Pending");
        (await approvals.GetHistoryAsync("FourMChange", id)).Should().ContainSingle();
    }

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString,
    };
}
