namespace NexaOne.SYS.Application.Deploys;

/// <summary>
/// 배포 파일 바이너리 보관소 (설계서 20.11).
/// 웹 적응: 현행 서버 공유 폴더 → API 서버 디스크. 구현은 API 계층(FileSystemDeployFileStorage).
/// </summary>
public interface IDeployFileStorage
{
    /// <summary>스트림 전체를 fileId 키로 저장하면서 SHA-256을 계산한다.</summary>
    Task<StoredDeployFile> SaveAsync(string fileId, Stream content, CancellationToken ct = default);

    /// <summary>저장된 파일의 읽기 스트림. 없으면 null.</summary>
    Stream? OpenRead(string fileId);

    /// <summary>저장 파일 삭제. 없으면 무시한다 (메타 기록 실패 시 고아 파일 정리용).</summary>
    void Delete(string fileId);
}

/// <summary>저장 결과 — SHA-256 hex(소문자)와 바이트 크기.</summary>
public sealed record StoredDeployFile(string Hash, long Size);
