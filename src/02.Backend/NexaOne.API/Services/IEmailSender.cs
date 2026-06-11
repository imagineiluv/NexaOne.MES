namespace NexaOne.API.Services;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken ct = default);
}
