namespace NexaOne.Web.Services;

public sealed class WaitOverlayService
{
    private int _depth;

    public bool IsVisible => _depth > 0;
    public string Message { get; private set; } = string.Empty;

    public event Action? StateChanged;

    public IDisposable Show(string message = "처리 중...")
    {
        Message = message;
        _depth++;
        StateChanged?.Invoke();
        return new Scope(this);
    }

    private void Release()
    {
        if (_depth > 0) _depth--;
        if (_depth == 0) Message = string.Empty;
        StateChanged?.Invoke();
    }

    private sealed class Scope(WaitOverlayService svc) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            svc.Release();
        }
    }
}
