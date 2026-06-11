namespace NexaOne.Common;

public abstract class AuditableEntity<TId> : AggregateRoot<TId>
    where TId : notnull
{
    protected AuditableEntity(TId id) : base(id) { }

    public DateTime CreatedAt { get; protected set; } = DateTime.UtcNow;
    public string CreatedBy { get; protected set; } = string.Empty;
    public DateTime? UpdatedAt { get; protected set; }
    public string? UpdatedBy { get; protected set; }
    public bool IsDeleted { get; protected set; }
    public DateTime? DeletedAt { get; protected set; }

    protected void SetAudit(string createdBy)
    {
        CreatedBy = createdBy;
        CreatedAt = DateTime.UtcNow;
    }

    protected void UpdateAudit(string updatedBy)
    {
        UpdatedBy = updatedBy;
        UpdatedAt = DateTime.UtcNow;
    }
}
