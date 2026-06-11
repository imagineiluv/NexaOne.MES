using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace NexaOne.Driver.OpcUa;

public sealed class OpcUaDriver : IDisposable
{
    private readonly ILogger<OpcUaDriver> _logger;
    private Session? _session;
    private Subscription? _subscription;
    private bool _disposed;

    public event EventHandler<OpcUaDataChange>? DataChanged;
    public bool IsConnected => _session?.Connected ?? false;

    public OpcUaDriver(ILogger<OpcUaDriver> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(
        string endpointUrl,
        string applicationName = "NexaOneMES",
        bool autoAcceptCertificate = true,
        CancellationToken ct = default)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationUri  = $"urn:{System.Net.Dns.GetHostName()}:{applicationName}",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType    = CertificateStoreType.X509Store,
                    StorePath    = "CurrentUser\\My",
                    SubjectName  = applicationName
                },
                AutoAcceptUntrustedCertificates = autoAcceptCertificate
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas         = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration     = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
        };

        await config.Validate(ApplicationType.Client);

        if (autoAcceptCertificate)
            config.CertificateValidator.CertificateValidation +=
                (_, e) => e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted);

        var endpointDesc = CoreClientUtils.SelectEndpoint(config, endpointUrl, useSecurity: false);
        var endpoint     = new ConfiguredEndpoint(null, endpointDesc, EndpointConfiguration.Create(config));

        _session = await Session.Create(config, endpoint, false,
            applicationName, 60_000, new UserIdentity(new AnonymousIdentityToken()), null);

        _logger.LogInformation("OPC-UA connected to {Endpoint}", endpointUrl);
    }

    public async Task<object?> ReadAsync(string nodeId, CancellationToken ct = default)
    {
        EnsureConnected();
        var node   = new NodeId(nodeId);
        var result = await _session!.ReadValueAsync(node, ct);
        if (StatusCode.IsBad(result.StatusCode))
            throw new ServiceResultException(result.StatusCode.Code);
        _logger.LogDebug("OPC-UA read {NodeId} = {Value}", nodeId, result.Value);
        return result.Value;
    }

    public async Task<T?> ReadAsync<T>(string nodeId, CancellationToken ct = default)
    {
        var value = await ReadAsync(nodeId, ct);
        if (value is null) return default;
        return (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task WriteAsync(string nodeId, object value, CancellationToken ct = default)
    {
        EnsureConnected();
        var node = new NodeId(nodeId);
        var writeValue = new WriteValue
        {
            NodeId      = node,
            AttributeId = Attributes.Value,
            Value       = new DataValue(new Variant(value))
        };

        var writeValues    = new WriteValueCollection { writeValue };
        var writeResponse  = await _session!.WriteAsync(null, writeValues, ct);
        if (StatusCode.IsBad(writeResponse.Results[0]))
            throw new ServiceResultException(writeResponse.Results[0].Code);
        _logger.LogDebug("OPC-UA write {NodeId} = {Value}", nodeId, value);
    }

    public async Task<IReadOnlyList<(string NodeId, object? Value)>> ReadMultipleAsync(
        IEnumerable<string> nodeIds, CancellationToken ct = default)
    {
        EnsureConnected();
        var ids = nodeIds.Select(id => new NodeId(id)).ToList();
        var readValues = new ReadValueIdCollection(ids.Select(n => new ReadValueId
        {
            NodeId      = n,
            AttributeId = Attributes.Value
        }));

        var response = await _session!.ReadAsync(null, 0, TimestampsToReturn.Both, readValues, ct);
        return response.Results
            .Select((r, i) => (ids[i].ToString(), StatusCode.IsGood(r.StatusCode) ? r.Value : null))
            .ToList();
    }

    public void Subscribe(IEnumerable<string> nodeIds, int publishingIntervalMs = 500)
    {
        EnsureConnected();

        _subscription = new Subscription(_session!.DefaultSubscription)
        {
            PublishingInterval = publishingIntervalMs,
            PublishingEnabled  = true
        };

        foreach (var nodeId in nodeIds)
        {
            var item = new MonitoredItem(_subscription.DefaultItem)
            {
                DisplayName   = nodeId,
                StartNodeId   = new NodeId(nodeId),
                AttributeId   = Attributes.Value,
                SamplingInterval = publishingIntervalMs
            };
            item.Notification += OnMonitoredItemNotification;
            _subscription.AddItem(item);
        }

        _session!.AddSubscription(_subscription);
        _subscription.Create();
        _logger.LogInformation("OPC-UA subscribed to {Count} nodes", _subscription.MonitoredItemCount);
    }

    public void Unsubscribe()
    {
        if (_subscription is null) return;
        _subscription.Delete(true);
        _session?.RemoveSubscription(_subscription);
        _subscription = null;
        _logger.LogInformation("OPC-UA subscription removed");
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_session is null) return;
        Unsubscribe();
        await _session.CloseAsync(ct);
        _session.Dispose();
        _session = null;
        _logger.LogInformation("OPC-UA disconnected");
    }

    private void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        foreach (var value in item.DequeueValues())
        {
            DataChanged?.Invoke(this, new OpcUaDataChange(
                item.StartNodeId.ToString() ?? item.DisplayName,
                value.Value,
                value.SourceTimestamp,
                StatusCode.IsGood(value.StatusCode)));
        }
    }

    private void EnsureConnected()
    {
        if (_session is null || !_session.Connected)
            throw new InvalidOperationException("OpcUaDriver is not connected.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _subscription?.Dispose();
        _session?.Dispose();
        _disposed = true;
    }
}

public sealed record OpcUaDataChange(
    string NodeId,
    object? Value,
    DateTime Timestamp,
    bool IsGood);
