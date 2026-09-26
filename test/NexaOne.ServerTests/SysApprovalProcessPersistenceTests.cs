using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;
using NexaOne.SYS.Infrastructure;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>SYS-owned generic approval: Pending single-request, guarded transitions, idempotent replays.</summary>
public sealed class SysApprovalProcessPersistenceTests
    : IClassFixture<SysApprovalProcessPersistenceTests.ApprovalFactory>
{
    private const string Secret = "sys-approval-persistence-jwt-secret-32bytes+!!";
    private const string Issuer = "nexaone-sys-approval-persistence-test";
    private readonly ApprovalFactory _factory;

    public SysApprovalProcessPersistenceTests(ApprovalFactory factory) => _factory = factory;

    public sealed class ApprovalFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-appr-{Guid.NewGuid():N}.db");
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
    public async Task Submit_decide_and_history_follow_the_pending_state_machine()
    {
        _ = _factory.CreateClient();
        var approvals = Process();

        var approvalId = await approvals.SubmitAsync(
            new ApprovalRequest("Recipe", "RCP-1", "레시피 v3"), "requester1", "sub-1", "hash-sub-1");
        approvalId.Should().StartWith("APR_");

        var pending = await approvals.GetCurrentAsync("Recipe", "RCP-1");
        pending!.Status.Should().Be("Pending");
        (await approvals.ListPendingAsync("Recipe")).Should().ContainSingle(r => r.ApprovalId == approvalId);

        await approvals.DecideAsync(
            new ApprovalDecision(approvalId, Approve: true, "looks good"),
            "approver1", "dec-1", "hash-dec-1");

        var decided = await approvals.GetCurrentAsync("Recipe", "RCP-1");
        decided!.Status.Should().Be("Approved");
        decided.DecidedBy.Should().Be("approver1");
        decided.Comment.Should().Be("looks good");
        (await approvals.ListPendingAsync()).Should().NotContain(r => r.ApprovalId == approvalId);

        var history = await approvals.GetHistoryAsync("Recipe", "RCP-1");
        history.Select(h => (h.FromStatus, h.ToStatus)).Should().Equal(
            [("New", "Pending"), ("Pending", "Approved")]);

        var second = () => approvals.DecideAsync(
            new ApprovalDecision(approvalId, Approve: false), "approver2", "dec-2", "hash-dec-2");
        await second.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already Approved*", "결정된 요청의 재결정은 상태기가 거절한다");
    }

    [Fact]
    public async Task Rejected_documents_can_be_resubmitted_and_replays_are_deduplicated()
    {
        _ = _factory.CreateClient();
        var approvals = Process();

        var approvalId = await approvals.SubmitAsync(
            new ApprovalRequest("FourMChange", "4M-9"), "requester1", "sub-9", "hash-sub-9");
        await approvals.DecideAsync(
            new ApprovalDecision(approvalId, Approve: false, "redo"), "approver1", "dec-9", "hash-dec-9");

        var resubmitted = await approvals.SubmitAsync(
            new ApprovalRequest("FourMChange", "4M-9", "rev2"), "requester1", "sub-10", "hash-sub-10");
        resubmitted.Should().Be(approvalId, "같은 문서의 재요청은 새 행이 아니라 같은 요청을 Pending으로 되돌린다");
        (await approvals.GetCurrentAsync("FourMChange", "4M-9"))!.Status.Should().Be("Pending");

        // 결정 멱등 재시도 — 같은 키+해시면 조용히 통과한다.
        await approvals.DecideAsync(
            new ApprovalDecision(approvalId, Approve: true), "approver1", "dec-10", "hash-dec-10");
        await approvals.DecideAsync(
            new ApprovalDecision(approvalId, Approve: true), "approver1", "dec-10", "hash-dec-10");

        var conflict = () => approvals.DecideAsync(
            new ApprovalDecision(approvalId, Approve: false), "approver1", "dec-10", "hash-other");
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*APPROVAL_REQUEST_CONFLICT*", "같은 키의 다른 해시는 충돌이다");

        var duplicatePending = () => approvals.SubmitAsync(
            new ApprovalRequest("FourMChange", "4M-9", "rev3"), "requester2", "sub-11", "hash-diff");
        await duplicatePending.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*APPROVAL_REQUEST_CONFLICT*");

        var history = await approvals.GetHistoryAsync("FourMChange", "4M-9");
        history.Select(h => h.ToStatus).Should().Equal(
            ["Pending", "Rejected", "Pending", "Approved"]);
    }

    [Fact]
    public async Task Requester_cancels_only_pending_requests()
    {
        _ = _factory.CreateClient();
        var approvals = Process();

        var approvalId = await approvals.SubmitAsync(
            new ApprovalRequest("PurchaseOrder", "PO-7"), "requester1", "sub-7", "hash-sub-7");
        await approvals.CancelAsync(approvalId, "requester1", "cxl-7", "hash-cxl-7");

        var record = await approvals.GetCurrentAsync("PurchaseOrder", "PO-7");
        record!.Status.Should().Be("Cancelled");
        var again = () => approvals.CancelAsync(approvalId, "requester1", "cxl-8", "hash-cxl-8");
        await again.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already Cancelled*");
    }

    private ApprovalProcess Process() => new(DataSource());

    private EesDataSource DataSource() => new()
    {
        Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
        ConnectionString = _factory.ConnString,
    };
}
