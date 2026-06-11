using Microsoft.AspNetCore.SignalR.Client;

namespace NexaOne.Web.Services;

/// <summary>실시간 허브 연결 상태 (설계서 §20.9).</summary>
public enum HubStatus
{
    /// <summary>연결 시도 전 또는 의도적 종료(로그아웃) 상태.</summary>
    Idle,

    /// <summary>최초 연결 또는 수동 재연결 시도 중.</summary>
    Connecting,

    Connected,

    /// <summary>자동 재연결 진행 중 — 화면 상단에 진행 상태를 표시한다 (§20.9).</summary>
    Reconnecting,

    /// <summary>연결 실패 또는 자동 재연결 횟수 소진 — 폴백 조회 모드로 전환한다 (§20.9).</summary>
    Disconnected,
}

/// <summary>
/// 허브 연결 상태 구독 지점. <see cref="Realtime.FallbackPoller"/> 등이 의존한다 (테스트에서 대체 가능).
/// </summary>
public interface IHubStatusSource
{
    HubStatus Status { get; }

    /// <summary>백그라운드 스레드에서 호출될 수 있다 — Blazor 컴포넌트에서는 InvokeAsync로 감쌀 것 (§20.9).</summary>
    event Action<HubStatus>? OnStatusChanged;
}

public sealed class NexaHubService : IHubStatusSource, IAsyncDisposable
{
    private readonly string _hubUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // §20.9: 재연결 성공 시 기존 구독 그룹을 모두 재구독하기 위해 추적한다
    private readonly HashSet<string> _groups = new(StringComparer.OrdinalIgnoreCase);

    private HubConnection? _connection;
    private Func<Task<string?>>? _tokenProvider;

    public event Action? OnAlarmUpdated;
    public event Action? OnWorkOrderUpdated;
    public event Action? OnDashboardRefresh;
    public event Action<string, string>? OnEquipmentStateChanged;
    public event Action<FdcDataPayload>? OnFdcDataReceived;
    public event Action<InterlockTriggeredPayload>? OnInterlockTriggered;
    public event Action<HubStatus>? OnStatusChanged;

    public HubStatus Status { get; private set; } = HubStatus.Idle;
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public NexaHubService(IConfiguration config)
    {
        var apiBase = config["ApiBaseUrl"] ?? throw new InvalidOperationException("ApiBaseUrl is required");
        _hubUrl = $"{apiBase.TrimEnd('/')}/hubs/smartees";
    }

    public async Task ConnectAsync(Func<Task<string?>> tokenProvider, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is not null) await TeardownAsync();
            _tokenProvider = tokenProvider;
            SetStatus(HubStatus.Connecting);

            _connection = new HubConnectionBuilder()
                .WithUrl(_hubUrl, options =>
                {
                    // 재연결 협상 시에도 호출되므로 항상 현재 토큰을 공급한다 (§20.9)
                    options.AccessTokenProvider = () =>
                        _tokenProvider?.Invoke() ?? Task.FromResult<string?>(null);
                })
                .WithAutomaticReconnect()   // 기본 0·2·10·30초 4회 — 소진 시 Closed → 폴백 전환 (§20.9)
                .Build();

            RegisterHandlers(_connection);

            try
            {
                await _connection.StartAsync(ct);
                await RejoinGroupsAsync(_connection);
                SetStatus(HubStatus.Connected);
            }
            catch
            {
                // 비치명: 실시간 없이도 동작한다 — 폴백 조회 모드 + 수동 재연결 버튼 노출 (§20.9)
                SetStatus(HubStatus.Disconnected);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>수동 재연결 (§20.9: 자동 재연결 소진 후 사용자가 버튼으로 재시도).</summary>
    public async Task<bool> ReconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is null || _tokenProvider is null) return false;
            if (_connection.State == HubConnectionState.Connected)
            {
                SetStatus(HubStatus.Connected);   // 표시 상태가 어긋나 있었다면 실상태로 동기화
                return true;
            }
            if (_connection.State != HubConnectionState.Disconnected) return false;   // 자동 재연결 진행 중

            SetStatus(HubStatus.Connecting);
            try
            {
                await _connection.StartAsync(ct);
                await RejoinGroupsAsync(_connection);
                SetStatus(HubStatus.Connected);
                return true;
            }
            catch
            {
                SetStatus(HubStatus.Disconnected);
                return false;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 그룹 구독 + 추적. 연결이 없으면 추적만 해 두고 (재)연결 성공 시 일괄 구독된다 (§20.9).
    /// 현재 알림은 전부 Clients.All 브로드캐스트이고 유일한 그룹(user:{userId})은 서버
    /// OnConnectedAsync가 재가입시키므로 호출처가 없다 — plant 등 그룹 스코프 알림 도입 대비 인프라.
    /// </summary>
    public async Task JoinGroupAsync(string groupName)
    {
        lock (_groups) _groups.Add(groupName);

        var conn = _connection;
        if (conn?.State == HubConnectionState.Connected)
        {
            try { await conn.InvokeAsync("JoinGroup", groupName); }
            catch { /* 전송 실패 시 재연결 핸들러가 재구독한다 */ }
        }
    }

    public async Task LeaveGroupAsync(string groupName)
    {
        lock (_groups) _groups.Remove(groupName);

        var conn = _connection;
        if (conn?.State == HubConnectionState.Connected)
        {
            try { await conn.InvokeAsync("LeaveGroup", groupName); }
            catch { /* 연결이 끊겼다면 서버 그룹 멤버십도 함께 소멸한다 */ }
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { await TeardownAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    private void RegisterHandlers(HubConnection conn)
    {
        conn.On("AlarmUpdated", () => OnAlarmUpdated?.Invoke());
        conn.On("WorkOrderUpdated", () => OnWorkOrderUpdated?.Invoke());
        conn.On("DashboardRefresh", () => OnDashboardRefresh?.Invoke());
        conn.On<EquipmentStateChangedPayload>("EquipmentStateChanged",
            p => OnEquipmentStateChanged?.Invoke(p.EquipmentId, p.NewState));
        conn.On<FdcDataPayload>("FdcDataReceived",
            p => OnFdcDataReceived?.Invoke(p));
        conn.On<InterlockTriggeredPayload>("InterlockTriggered",
            p => OnInterlockTriggered?.Invoke(p));

        // §20.9: HubConnection.Reconnecting / Reconnected / Closed 이벤트로 연결 끊김 재시도를 처리한다.
        // ConnectAsync가 옛 연결을 교체한 뒤 옛 연결의 수명주기 이벤트가 뒤늦게 도착할 수 있으므로,
        // 현재 연결의 이벤트만 공유 Status에 반영한다 (옛 연결의 Closed가 상태를 오염시키지 않도록).
        conn.Reconnecting += _ =>
        {
            if (ReferenceEquals(conn, _connection))
                SetStatus(HubStatus.Reconnecting);
            return Task.CompletedTask;
        };
        conn.Reconnected += async _ =>
        {
            if (!ReferenceEquals(conn, _connection)) return;

            // 재연결되면 connectionId가 바뀌어 서버 그룹 멤버십이 사라지므로 전부 재구독한다 (§20.9)
            await RejoinGroupsAsync(conn);

            // 재구독 중 연결이 다시 끊겼을 수 있다 — 실제 Connected일 때만 표시 (플래핑 시 가짜 Connected 방지)
            if (ReferenceEquals(conn, _connection) && conn.State == HubConnectionState.Connected)
                SetStatus(HubStatus.Connected);
        };
        conn.Closed += _ =>
        {
            // 자동 재연결 소진(또는 치명 오류). 의도적 종료(Idle 선표시)는 폴백으로 전환하지 않는다 (§20.9)
            if (ReferenceEquals(conn, _connection) && Status != HubStatus.Idle)
                SetStatus(HubStatus.Disconnected);
            return Task.CompletedTask;
        };
    }

    private async Task RejoinGroupsAsync(HubConnection conn)
    {
        string[] groups;
        lock (_groups) groups = _groups.ToArray();

        foreach (var group in groups)
        {
            try { await conn.InvokeAsync("JoinGroup", group); }
            catch { /* 다음 재연결에서 재시도 */ }
        }
    }

    private async Task TeardownAsync()
    {
        SetStatus(HubStatus.Idle);   // 의도적 종료 — Closed 핸들러가 폴백 모드로 전환하지 않도록 먼저 표시
        if (_connection is null) return;

        try { await _connection.StopAsync(); } catch { }
        await _connection.DisposeAsync();
        _connection = null;
    }

    private void SetStatus(HubStatus status)
    {
        if (Status == status) return;
        Status = status;
        OnStatusChanged?.Invoke(status);
    }

    private sealed record EquipmentStateChangedPayload(string EquipmentId, string NewState);

    public sealed record FdcDataPayload(string EquipmentId, string ParameterId, decimal Value, bool IsOutOfSpec);
    public sealed record InterlockTriggeredPayload(string EquipmentId, string ParameterId, string Action, string Message);
}
