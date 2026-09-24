using FluentAssertions;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Erp;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class BillingPdfRendererTests
{
    [Fact]
    public void Korean_invoice_renders_as_a_pdf_with_multiple_pages()
    {
        var scope = new BusinessScope("NexaOne.MES", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var lines = Enumerable.Range(1, 80)
            .Select(index => new BillingLine($"한글 유지보수 서비스 항목 {index} — 긴 설명과 페이지 나눔 검증", 1234.56m, index))
            .ToArray();
        var input = new BillingDocumentInput(Guid.NewGuid(), new(2026, 9, 24), new(2026, 10, 24), "KRW",
            lines, Terms: "납기 후 30일 이내 입금해 주세요.", Note: "문의: 넥사원 회계팀");
        var totals = new BillingTotals(lines.Sum(line => line.LineTotal), 0m, 0m,
            lines.Sum(line => line.LineTotal));
        var document = new BillingDocument(Guid.NewGuid(), scope, Guid.NewGuid(), Guid.NewGuid(),
            BillingKind.Invoice, 42, input, totals, BillingStatus.Sent, Guid.NewGuid().ToString("D"));
        var view = new BillingDocumentView(document,
            new(Guid.NewGuid(), Guid.NewGuid(), "CUST-1", "가나다 주식회사", true));
        var root = FindRepositoryRoot();
        var font = Path.Combine(root, "src", "00.Main", "NexaOne.Server", "wwwroot", "fonts",
            "Pretendard-Regular.ttf");

        var bytes = BillingPdfRenderer.Render(view, font);

        bytes.Should().StartWith(new byte[] { 0x25, 0x50, 0x44, 0x46 });
        bytes.Length.Should().BeGreaterThan(20_000);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NexaOne.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
