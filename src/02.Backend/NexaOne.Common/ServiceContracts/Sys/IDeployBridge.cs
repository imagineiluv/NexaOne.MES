using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sys;

/// <summary>배포 파일 스냅샷(SYS_DEPLOY_FILE, §20.11) — Hash는 SHA-256 hex(클라이언트 무결성 검증용).</summary>
public record DeployFileDto(
    string FileId, string Version, string FileName, string Hash, long FileSize,
    string Description, bool ForceUpdate, bool IsActive, string UploadedBy, DateTime UploadedAt);

/// <summary>다운로드 재료 — 메타 + 열린 읽기 스트림(폐기는 호출자 책임, 컨트롤러가 응답으로 소유 이전).</summary>
public sealed record DeployDownloadDto(DeployFileDto File, Stream Content);

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — 클라이언트 자동 업데이트 배포(§20.11). plugin(SYS)의
/// DeployService(버전 형식/파일명 검증·System.Version 기반 latest 선정·비활성 회수·바이너리 저장/정리)에
/// 위임한다. Stream은 공유 시스템 타입이라 ALC 경계를 안전하게 넘는다(업로드는 요청 스트림, 다운로드는
/// 저장소 읽기 스트림). 순수 목록 조회도 latest 선정과 함께 서비스가 소유한다(정렬 규칙 단일 출처).</summary>
public interface IDeployBridge : INexaModuleBridge
{
    Task<Result<DeployFileDto>> UploadAsync(
        string version, string fileName, string? description, bool forceUpdate,
        Stream content, string uploadedBy, CancellationToken ct = default);
    Task<Result<IReadOnlyList<DeployFileDto>>> GetAllAsync(CancellationToken ct = default);
    Task<Result<DeployFileDto>> GetLatestAsync(CancellationToken ct = default);
    Task<Result> SetActiveAsync(string fileId, bool isActive, CancellationToken ct = default);
    Task<Result<DeployDownloadDto>> OpenDownloadAsync(string fileId, CancellationToken ct = default);
}
