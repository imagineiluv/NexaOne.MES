using NexaOne.Driver.SmtpEmail;

namespace NexaOne.API.Services;

public sealed class SmtpEmailSender : IEmailSender, IDisposable
{
    private readonly SmtpEmailDriver _driver;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailDriver> driverLogger)
    {
        _driver = new SmtpEmailDriver(driverLogger);

        var section = configuration.GetSection("Smtp");
        _driver.Configure(
            host:            section["Host"]            ?? "localhost",
            port:            int.Parse(section["Port"]  ?? "587"),
            userName:        section["UserName"]        ?? string.Empty,
            password:        section["Password"]        ?? string.Empty,
            fromAddress:     section["FromAddress"]     ?? "noreply@nexaone.local",
            fromDisplayName: section["FromDisplayName"] ?? "NexaOne MES",
            useSsl:          bool.Parse(section["UseSsl"] ?? "true"));
    }

    public Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken ct = default)
        => _driver.SendAsync(to, subject, body, isHtml, ct: ct);

    public void Dispose() => _driver.Dispose();
}
