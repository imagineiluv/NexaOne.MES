using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Nexa.Components.Email;
using NexaFramework.Service.Collaboration;
using NexaOne.ServiceContracts.Collaboration;

namespace NexaOne.Server.Gateway;

/// <summary>
/// Resolves opaque delivery credential references from host configuration. Secret values never cross this host
/// adapter and the configuration is read for every attempt so credential rotation does not require database writes.
/// </summary>
public sealed class ConfigurationEmailCredentialResolver(IConfiguration configuration)
{
    private readonly IConfiguration _configuration = configuration
        ?? throw new ArgumentNullException(nameof(configuration));

    internal EmailDeliveryCredential Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new DeliveryProviderException(DeliveryProviderError.CredentialUnavailable);
        var matches = _configuration.GetSection("Delivery:Email:Credentials").GetChildren()
            .Where(section => string.Equals(section["Reference"], reference, StringComparison.Ordinal))
            .Take(2).ToArray();
        if (matches.Length == 0)
            throw new DeliveryProviderException(DeliveryProviderError.CredentialUnavailable);
        if (matches.Length != 1)
            throw new DeliveryProviderException(DeliveryProviderError.ConfigurationInvalid);

        var section = matches[0];
        var host = section["Host"]?.Trim();
        var userName = section["UserName"];
        var password = section["Password"];
        var fromAddress = section["FromAddress"]?.Trim();
        var fromDisplayName = section["FromDisplayName"]?.Trim() ?? string.Empty;
        var port = section.GetValue<int?>("Port");
        var timeoutSeconds = section.GetValue("TimeoutSeconds", 30);
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown
            || port is null or < 1 or > IPEndPoint.MaxPort
            || string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)
            || string.IsNullOrWhiteSpace(fromAddress)
            || !MailAddress.TryCreate(fromAddress, out var parsed)
            || !string.Equals(parsed.Address, fromAddress, StringComparison.OrdinalIgnoreCase)
            || parsed.Host.Length == 0 || Uri.CheckHostName(parsed.Host) == UriHostNameType.Unknown
            || timeoutSeconds is < 1 or > 300)
            throw new DeliveryProviderException(DeliveryProviderError.ConfigurationInvalid);

        return new(host, port.Value, userName, password, fromAddress, fromDisplayName,
            section.GetValue("UseStartTls", true), TimeSpan.FromSeconds(timeoutSeconds));
    }
}

internal sealed record EmailDeliveryCredential(
    string Host, int Port, string UserName, string Password, string FromAddress,
    string FromDisplayName, bool UseStartTls, TimeSpan Timeout);

/// <summary>SMTP provider backed by the standalone Framework EmailComponent.</summary>
public sealed class EmailComponentDeliveryProvider(ConfigurationEmailCredentialResolver credentials)
    : IDeliveryProvider
{
    public string Key => "smtp";

    public async Task<string> SendAsync(DeliveryPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Channel != DeliveryChannel.Email)
            throw new DeliveryProviderException(DeliveryProviderError.ConfigurationInvalid);
        var credential = credentials.Resolve(payload.CredentialReference);
        try
        {
            using var sender = EmailComponent.Create(new EmailComponentOptions
            {
                Host = credential.Host,
                Port = credential.Port,
                UserName = credential.UserName,
                Password = credential.Password,
                FromAddress = credential.FromAddress,
                FromDisplayName = credential.FromDisplayName,
                UseStartTls = credential.UseStartTls,
                Timeout = credential.Timeout,
            });
            var result = await sender.SendAsync(
                new EmailMessage(payload.Recipient, payload.Subject, payload.Body), ct);
            if (result.RecipientCount != 1)
                throw new DeliveryProviderException(DeliveryProviderError.SendFailed);
            return "smtp:" + Guid.NewGuid().ToString("N");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (EmailComponentException error)
        {
            throw new DeliveryProviderException(error.Code == EmailErrorCode.InvalidAddress
                ? DeliveryProviderError.InvalidRecipient
                : DeliveryProviderError.SendFailed, error);
        }
        catch (DeliveryProviderException) { throw; }
        catch (ArgumentException error)
        {
            throw new DeliveryProviderException(DeliveryProviderError.ConfigurationInvalid, error);
        }
    }
}

/// <summary>Exact-key provider registry used by the delivery worker.</summary>
public sealed class DeliveryProviderRegistry(IDeliveryProvider provider) : IDeliveryProviderRegistry
{
    private readonly IDeliveryProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public IDeliveryProvider GetRequired(string providerKey)
        => string.Equals(providerKey, _provider.Key, StringComparison.Ordinal)
            ? _provider
            : throw new DeliveryProviderException(DeliveryProviderError.UnsupportedProvider);
}
