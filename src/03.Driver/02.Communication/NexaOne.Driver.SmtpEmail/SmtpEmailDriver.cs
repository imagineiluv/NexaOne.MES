using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace NexaOne.Driver.SmtpEmail;

public sealed class SmtpEmailDriver : IDisposable
{
    private readonly ILogger<SmtpEmailDriver> _logger;
    private SmtpConfig? _config;
    private bool _disposed;

    public SmtpEmailDriver(ILogger<SmtpEmailDriver> logger)
    {
        _logger = logger;
    }

    public void Configure(
        string host,
        int port,
        string userName,
        string password,
        string fromAddress,
        string fromDisplayName,
        bool useSsl = true)
    {
        _config = new SmtpConfig(host, port, userName, password, fromAddress, fromDisplayName, useSsl);
    }

    public async Task SendAsync(
        string to,
        string subject,
        string body,
        bool isHtml = false,
        IEnumerable<string>? cc = null,
        IEnumerable<(string FileName, Stream Content, string ContentType)>? attachments = null,
        CancellationToken ct = default)
    {
        if (_config is null) throw new InvalidOperationException("SmtpEmailDriver not configured.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_config.FromDisplayName, _config.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));

        if (cc is not null)
            foreach (var addr in cc)
                message.Cc.Add(MailboxAddress.Parse(addr));

        message.Subject = subject;

        var builder = new BodyBuilder();
        if (isHtml)
            builder.HtmlBody = body;
        else
            builder.TextBody = body;

        if (attachments is not null)
            foreach (var (fileName, content, contentType) in attachments)
                builder.Attachments.Add(fileName, content, ContentType.Parse(contentType));

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            var secureOption = _config.UseSsl
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.None;

            await client.ConnectAsync(_config.Host, _config.Port, secureOption, ct);
            await client.AuthenticateAsync(_config.UserName, _config.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("Email sent to {To} subject: {Subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}", to);
            throw;
        }
    }

    public async Task SendToManyAsync(
        IEnumerable<string> recipients,
        string subject,
        string body,
        bool isHtml = false,
        CancellationToken ct = default)
    {
        if (_config is null) throw new InvalidOperationException("SmtpEmailDriver not configured.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_config.FromDisplayName, _config.FromAddress));
        foreach (var addr in recipients) message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = subject;

        var builder = new BodyBuilder();
        if (isHtml) builder.HtmlBody = body; else builder.TextBody = body;
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            var secureOption = _config.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
            await client.ConnectAsync(_config.Host, _config.Port, secureOption, ct);
            await client.AuthenticateAsync(_config.UserName, _config.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            _logger.LogInformation("Email sent to {Count} recipients, subject: {Subject}", message.To.Count, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send bulk email");
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private sealed record SmtpConfig(
        string Host,
        int Port,
        string UserName,
        string Password,
        string FromAddress,
        string FromDisplayName,
        bool UseSsl);
}
