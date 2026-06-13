using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Services;
using NexusCom.Notify.SmtpEmail;

namespace NexaOne.UnitTests.Services;

/// <summary>SMTP 알림 글루(NexusCom.Notify.SmtpEmail 래핑)가 "Smtp" 설정 섹션을 읽어
/// IEmailSender로 초기화되는 계약을 검증한다 (실제 전송은 SMTP 서버 의존이라 통합 테스트 영역).</summary>
public sealed class SmtpEmailSenderTests
{
    [Fact]
    public void Constructs_as_email_sender_from_configuration_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"]        = "smtp.test.local",
                ["Smtp:Port"]        = "587",
                ["Smtp:FromAddress"] = "noreply@test.local",
                ["Smtp:UseSsl"]      = "true",
            })
            .Build();

        var sut = new SmtpEmailSender(config, NullLogger<SmtpEmailDriver>.Instance);

        sut.Should().BeAssignableTo<IEmailSender>("앱은 IEmailSender 계약으로만 글루를 소비한다");
    }

    [Fact]
    public void Constructs_with_defaults_when_section_missing()
    {
        var config = new ConfigurationBuilder().Build();

        var act = () => new SmtpEmailSender(config, NullLogger<SmtpEmailDriver>.Instance);

        act.Should().NotThrow("Smtp 섹션이 없어도 안전한 기본값으로 초기화된다");
    }
}
