using System.Threading.Channels;
using NexaOne.Application.Messaging;

namespace NexaOne.Server.Logging;

/// <summary>DB 로그 파이프라인(호스트 로깅 인프라) — ILogger Warning+ 항목을 SYS_APP_LOG(V064)에 기록해
/// LOG_VIEWER 화면의 데이터 원천이 된다. 기본 OFF — AppLogging:Db:Enabled=true로만 켠다.
/// 구조: DbLoggerProvider(enqueue, 논블로킹) → 유계 채널(1000, 가장 오래된 것부터 드롭) →
/// AppLogFlushWorker(IRuleDispatcher INSERT). 루프 방지: 자기 네임스페이스 카테고리는 기록하지 않고,
/// 플러시 실패는 ILogger가 아닌 Console로만 보고한다(기록 실패→로그→기록의 순환 차단).</summary>
public sealed record AppLogEntry(string Level, string Category, string Message, string? Exception, DateTime LoggedAt);

public sealed class DbLoggerProvider : ILoggerProvider
{
    private readonly ChannelWriter<AppLogEntry> _writer;

    public DbLoggerProvider(ChannelWriter<AppLogEntry> writer) => _writer = writer;

    public ILogger CreateLogger(string categoryName) => new DbLogger(categoryName, _writer);

    public void Dispose() { }

    private sealed class DbLogger : ILogger
    {
        private readonly string _category;
        private readonly ChannelWriter<AppLogEntry> _writer;

        public DbLogger(string category, ChannelWriter<AppLogEntry> writer)
        {
            _category = category;
            _writer = writer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Warning
               && !_category.StartsWith("NexaOne.Server.Logging", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;
            // 유계 채널(DropOldest) — 가득 차면 오래된 항목을 버리고 진행(로깅이 요청 처리를 절대 막지 않음).
            _writer.TryWrite(new AppLogEntry(
                logLevel.ToString(),
                _category.Length > 300 ? _category[..300] : _category,
                message.Length > 2000 ? message[..2000] : message,
                exception?.ToString() is { } ex ? (ex.Length > 4000 ? ex[..4000] : ex) : null,
                DateTime.UtcNow));
        }
    }
}

/// <summary>채널의 로그 항목을 SYS_APP_LOG로 플러시하는 워커. 항목 단위 INSERT(경고+는 저빈도라 충분).</summary>
public sealed class AppLogFlushWorker : BackgroundService
{
    private const string InsertSql = @"
        INSERT INTO SYS_APP_LOG (LOG_ID, LOG_LEVEL, CATEGORY, MESSAGE, EXCEPTION, LOGGED_AT)
        VALUES (@id, @level, @category, @message, @exception, @at)";

    private readonly ChannelReader<AppLogEntry> _reader;
    private readonly IRuleDispatcher _dispatcher;

    public AppLogFlushWorker(ChannelReader<AppLogEntry> reader, IRuleDispatcher dispatcher)
    {
        _reader = reader;
        _dispatcher = dispatcher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in _reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _dispatcher.ExecuteAsync(InsertSql, new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["level"] = entry.Level,
                    ["category"] = entry.Category,
                    ["message"] = entry.Message,
                    ["exception"] = (object?)entry.Exception ?? DBNull.Value,
                    ["at"] = entry.LoggedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // ILogger 미사용(순환 차단) — Console로만 보고하고 다음 항목 진행.
                Console.WriteLine($"[AppLogFlushWorker] flush failed: {ex.Message}");
            }
        }
    }
}
