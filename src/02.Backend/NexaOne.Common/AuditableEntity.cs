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

    /// <summary>영속된 감사 메타데이터를 읽기경로 재구성 시 그대로 복원한다(읽기경로 Restore 패턴).
    /// 미복원 시 CreatedAt이 매 읽기마다 UtcNow로 재생성되고 CreatedBy=""·UpdatedBy/At=null로 리셋되는
    /// 상태손실이 생긴다(특히 도메인 객체를 직접 직렬화하는 응답에서 노출). 각 엔티티의 Restore 팩토리가 호출한다.</summary>
    protected void RestoreAudit(string createdBy, DateTime createdAt, string? updatedBy, DateTime? updatedAt)
    {
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    /// <summary>영속된 소프트삭제 상태(IsDeleted/DeletedAt)를 읽기경로 재구성 시 그대로 복원한다
    /// (IS_DELETED/DELETED_AT 컬럼이 있는 엔티티 한정). 필터 없는 GetById가 소프트삭제 행을 노출할 때
    /// 비삭제로 둔갑하거나 삭제시각이 손실되는 것을 막는다.</summary>
    protected void RestoreSoftDelete(bool isDeleted, DateTime? deletedAt)
    {
        IsDeleted = isDeleted;
        DeletedAt = deletedAt;
    }
}
