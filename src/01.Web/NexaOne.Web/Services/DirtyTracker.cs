namespace NexaOne.Web.Services;

public sealed class DirtyTracker
{
    private readonly HashSet<string> _dirtyKeys = [];

    public bool IsDirty => _dirtyKeys.Count > 0;

    public event Action? StateChanged;

    public void MarkDirty(string key = "default")
    {
        if (_dirtyKeys.Add(key))
            StateChanged?.Invoke();
    }

    public void MarkClean(string key = "default")
    {
        if (_dirtyKeys.Remove(key))
            StateChanged?.Invoke();
    }

    public void ClearAll()
    {
        _dirtyKeys.Clear();
        StateChanged?.Invoke();
    }
}
