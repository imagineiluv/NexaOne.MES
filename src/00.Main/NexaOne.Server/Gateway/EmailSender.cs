using System.Net;
using System.Net.Mail;

namespace NexaOne.Server.Gateway;

/// <summary>메일 발송 추상화(호스트 인프라) — 비밀번호 재설정 등 인증 흐름의 발신 채널. 기본은 무발송
/// (<see cref="NullEmailSender"/>)이며 Email:Smtp:Enabled=true이면 Host/Port/Sender를 모두 검증한 뒤
/// SMTP 실발송으로 전환한다. 활성화된 불완전 설정은 호스트 조립 시 실패한다.
/// 테스트는 이 인터페이스를 레코더로 대체해 토큰 전달을 검증한다.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>무발송 구현(기본) — 메일 인프라 없는 dev/CI에서 흐름을 깨지 않는다. 본문(토큰 포함)은 보안상 로그하지 않는다.</summary>
public sealed class NullEmailSender : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        Console.WriteLine($"[NullEmailSender] drop mail to={to} subject={subject} (Email:Smtp:Enabled=false)");
        return Task.CompletedTask;
    }
}

/// <summary>SMTP 실발송 — Email:Smtp:{Host,Port,Sender,User,Password,UseSsl}. 운영 메일서버 설정 시에만 사용.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _sender;
    private readonly string? _user;
    private readonly string? _password;
    private readonly bool _useSsl;

    public SmtpEmailSender(string host, int port, string sender, string? user, string? password, bool useSsl)
    {
        _host = host;
        _port = port;
        _sender = sender;
        _user = user;
        _password = password;
        _useSsl = useSsl;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        using var client = new SmtpClient(_host, _port) { EnableSsl = _useSsl };
        if (!string.IsNullOrEmpty(_user))
            client.Credentials = new NetworkCredential(_user, _password);
        using var message = new MailMessage(_sender, to, subject, body);
        await client.SendMailAsync(message, ct);
    }
}
