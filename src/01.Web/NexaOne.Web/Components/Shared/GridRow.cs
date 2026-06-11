namespace NexaOne.Web.Components.Shared;

public sealed class GridRow<T>
{
    public T Data { get; set; }
    public string State { get; internal set; } = RowState.Clean;

    public GridRow(T data) => Data = data;

    public bool IsClean   => State == RowState.Clean;
    public bool IsAdded   => State == RowState.Added;
    public bool IsModified => State == RowState.Modified;
    public bool IsDeleted  => State == RowState.Deleted;
}

public static class RowState
{
    public const string Clean    = "";
    public const string Added    = "added";
    public const string Modified = "modified";
    public const string Deleted  = "deleted";
}
