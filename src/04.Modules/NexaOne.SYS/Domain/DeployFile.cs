namespace NexaOne.SYS.Domain;

/// <summary>
/// 클라이언트 배포 파일 메타데이터 (설계서 20.11).
/// 바이너리는 IDeployFileStorage(API 서버 디스크)가 FileId를 키로 보관한다.
/// </summary>
public sealed class DeployFile
{
    private DeployFile() { }

    public string FileId { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    /// <summary>SHA-256 hex(소문자) — 클라이언트가 다운로드 후 무결성을 검증한다.</summary>
    public string Hash { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    /// <summary>릴리스 노트.</summary>
    public string Description { get; private set; } = string.Empty;
    /// <summary>true면 클라이언트가 업데이트를 건너뛸 수 없다.</summary>
    public bool ForceUpdate { get; private set; }
    /// <summary>문제 버전 회수용 — 비활성은 latest 선정/다운로드에서 제외된다.</summary>
    public bool IsActive { get; private set; }
    public string UploadedBy { get; private set; } = string.Empty;
    public DateTime UploadedAt { get; private set; }

    public static DeployFile Create(
        string fileId,
        string version,
        string fileName,
        string hash,
        long fileSize,
        string description,
        bool forceUpdate,
        string uploadedBy) =>
        new()
        {
            FileId = fileId,
            Version = version,
            FileName = fileName,
            Hash = hash,
            FileSize = fileSize,
            Description = description,
            ForceUpdate = forceUpdate,
            IsActive = true,
            UploadedBy = uploadedBy,
            UploadedAt = DateTime.UtcNow
        };

    /// <summary>DB 행 복원용 — 저장된 상태 그대로 재구성한다.</summary>
    public static DeployFile Restore(
        string fileId,
        string version,
        string fileName,
        string hash,
        long fileSize,
        string description,
        bool forceUpdate,
        bool isActive,
        string uploadedBy,
        DateTime uploadedAt) =>
        new()
        {
            FileId = fileId,
            Version = version,
            FileName = fileName,
            Hash = hash,
            FileSize = fileSize,
            Description = description,
            ForceUpdate = forceUpdate,
            IsActive = isActive,
            UploadedBy = uploadedBy,
            UploadedAt = uploadedAt
        };

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}
