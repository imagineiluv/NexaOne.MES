using NexaFramework.Service.Collaboration;

namespace NexaOne.ServiceContracts.Collaboration;

/// <summary>Host-owned registry of outbound providers. Provider keys are exact and case-sensitive.</summary>
public interface IDeliveryProviderRegistry
{
    IDeliveryProvider GetRequired(string providerKey);
}

/// <summary>External I/O boundary for one provider. Implementations must not log payload bodies or credentials.</summary>
public interface IDeliveryProvider
{
    string Key { get; }
    Task<string> SendAsync(DeliveryPayload payload, CancellationToken ct = default);
}

/// <summary>Stable categories emitted by host provider adapters without exposing provider exception text.</summary>
public enum DeliveryProviderError
{
    UnsupportedProvider,
    CredentialUnavailable,
    ConfigurationInvalid,
    InvalidRecipient,
    SendFailed
}

/// <summary>An expected provider failure whose category is safe to persist as a normalized code.</summary>
public sealed class DeliveryProviderException : Exception
{
    public DeliveryProviderException(DeliveryProviderError error)
        : base($"Outbound provider failed with {error}.") => Error = error;

    public DeliveryProviderException(DeliveryProviderError error, Exception innerException)
        : base($"Outbound provider failed with {error}.", innerException)
        => Error = error;

    public DeliveryProviderError Error { get; }
}
