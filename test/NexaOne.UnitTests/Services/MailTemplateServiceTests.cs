using NexaOne.API.Services;

namespace NexaOne.UnitTests.Services;

/// <summary>§20.10 — 메일 템플릿 로딩/치환. 현행 Config/Mail/{template}({culture}).xml 구조와
/// ${KEY} 플레이스홀더, ko-KR 폴백 규칙을 검증한다.</summary>
public sealed class MailTemplateServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "nexaone-mail-" + Guid.NewGuid().ToString("N"));

    public MailTemplateServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 임시 폴더 정리 실패는 무시 */ }
    }

    private MailTemplateService Build() => new(_dir);

    private void WriteTemplate(string culture, string content) =>
        File.WriteAllText(Path.Combine(_dir, $"InitPassword({culture}).xml"), content);

    [Fact]
    public void Render_existing_culture_substitutes_placeholders()
    {
        WriteTemplate("en-US", "<html>${USERNAME}: ${PASSWORD}</html>");

        var body = Build().Render("InitPassword", "en-US", new Dictionary<string, string>
        {
            ["USERNAME"] = "Alice",
            ["PASSWORD"] = "tmp123!"
        });

        body.Should().Be("<html>Alice: tmp123!</html>");
    }

    [Fact]
    public void Render_missing_culture_falls_back_to_koKR()
    {
        WriteTemplate("ko-KR", "<html>임시 비밀번호: ${PASSWORD}</html>");

        var body = Build().Render("InitPassword", "vi-VN", new Dictionary<string, string>
        {
            ["PASSWORD"] = "tmp123!"
        });

        body.Should().Be("<html>임시 비밀번호: tmp123!</html>");
    }

    [Fact]
    public void Render_missing_template_returns_null()
    {
        var body = Build().Render("InitPassword", "ko-KR", new Dictionary<string, string>());

        body.Should().BeNull("템플릿이 없으면 호출자가 평문 폴백을 쓰도록 null을 반환한다");
    }

    [Fact]
    public void Render_leaves_unknown_placeholders_untouched()
    {
        WriteTemplate("ko-KR", "${PASSWORD} / ${UNKNOWN}");

        var body = Build().Render("InitPassword", "ko-KR", new Dictionary<string, string>
        {
            ["PASSWORD"] = "x"
        });

        body.Should().Be("x / ${UNKNOWN}");
    }
}
