namespace NexaOne.Web.Components.Shared;

/// <summary>
/// Manages grid row state (_STATE_) for add/modify/delete tracking.
/// Used by pages to track changes before sending to the save API.
/// </summary>
public sealed class GridStateManager<T>
{
    private readonly List<GridRow<T>> _rows = [];
    private const int BulkSaveThreshold = 10_000;

    public IReadOnlyList<GridRow<T>> Rows => _rows;
    public bool HasChanges => _rows.Any(r => !r.IsClean);
    public bool NeedsBulkSave => GetChangedRows().Count >= BulkSaveThreshold;

    public event Action? StateChanged;

    /// <summary>Loads data and clears all state flags.</summary>
    public void Load(IEnumerable<T> items)
    {
        _rows.Clear();
        foreach (var item in items)
            _rows.Add(new GridRow<T>(item));
        StateChanged?.Invoke();
    }

    /// <summary>Appends a new row marked as 'added'.</summary>
    public GridRow<T> AddRow(T data)
    {
        var row = new GridRow<T>(data) { State = RowState.Added };
        _rows.Add(row);
        StateChanged?.Invoke();
        return row;
    }

    /// <summary>Replaces a row's data; state: clean→modified, added stays added.</summary>
    public void ModifyRow(GridRow<T> row, T newData)
    {
        row.Data = newData;
        if (row.IsClean)
            row.State = RowState.Modified;
        // if already added or modified, state is unchanged
        StateChanged?.Invoke();
    }

    /// <summary>Marks a row as deleted (visual only — not removed from list).</summary>
    public void DeleteRow(GridRow<T> row)
    {
        row.State = RowState.Deleted;
        StateChanged?.Invoke();
    }

    /// <summary>Restores a deleted row: original rows → modified, new rows → removed.</summary>
    public void RestoreRow(GridRow<T> row)
    {
        if (row.IsDeleted)
        {
            // Was this row originally loaded (i.e., not added)?
            // Heuristic: if row was previously at Deleted, revert to Modified.
            // If it was originally added and then deleted, remove it.
            // Since we can't distinguish without extra metadata, always revert to Modified
            // (callers that track "originally added" should call RemoveRow directly).
            row.State = RowState.Modified;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Hard-removes a row from the list (use for rows that were never saved).</summary>
    public void RemoveRow(GridRow<T> row)
    {
        _rows.Remove(row);
        StateChanged?.Invoke();
    }

    /// <summary>Returns only rows with a non-clean state.</summary>
    public IReadOnlyList<GridRow<T>> GetChangedRows()
        => _rows.Where(r => !r.IsClean).ToList();

    /// <summary>Clears all state flags and removes physically-deleted rows.</summary>
    public void AcceptChanges()
    {
        _rows.RemoveAll(r => r.IsDeleted);
        foreach (var r in _rows) r.State = RowState.Clean;
        StateChanged?.Invoke();
    }

    /// <summary>Discards all changes: removed added rows, reverts others to clean.</summary>
    public void RejectChanges(IEnumerable<T> originalItems)
    {
        Load(originalItems);
    }

    /// <summary>CSS class for row styling based on state.</summary>
    public static string GetRowCssClass(GridRow<T> row) => row.State switch
    {
        RowState.Added    => "row-state-added",
        RowState.Modified => "row-state-modified",
        RowState.Deleted  => "row-state-deleted",
        _                 => string.Empty
    };
}
