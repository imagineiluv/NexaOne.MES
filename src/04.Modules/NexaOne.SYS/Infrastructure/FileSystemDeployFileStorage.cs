using System.Security.Cryptography;
using NexaOne.SYS.Application.Deploys;

namespace NexaOne.SYS.Infrastructure;

/// <summary>배포 파일 바이너리 보관소의 파일시스템 구현(설계서 20.11) — 구 API 계층 구현의 모듈 이관.
/// fileId(GUID hex)를 파일명으로 baseDirectory에 보관하고, 저장 중 SHA-256을 스트리밍 계산한다
/// (대용량 재읽기 없음). fileId는 서비스가 생성한 GUID라 경로 성분이 없다(경로 주입 불가).</summary>
public sealed class FileSystemDeployFileStorage : IDeployFileStorage
{
    private readonly string _baseDirectory;

    /// <param name="baseDirectory">보관 루트(상대면 프로세스 작업 디렉터리 기준). 없으면 생성한다.</param>
    public FileSystemDeployFileStorage(string baseDirectory)
    {
        _baseDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(baseDirectory) ? "data/deploy-files" : baseDirectory);
        Directory.CreateDirectory(_baseDirectory);
    }

    public async Task<StoredDeployFile> SaveAsync(string fileId, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(fileId);
        using var sha = SHA256.Create();
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
        {
            await content.CopyToAsync(hashing, ct);
        }
        var size = new FileInfo(path).Length;
        var hash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
        return new StoredDeployFile(hash, size);
    }

    public Stream? OpenRead(string fileId)
    {
        var path = PathFor(fileId);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    public void Delete(string fileId)
    {
        try { File.Delete(PathFor(fileId)); } catch (IOException) { /* 정리 실패는 무시(고아 파일 잔존 허용) */ }
    }

    private string PathFor(string fileId) => Path.Combine(_baseDirectory, Path.GetFileName(fileId));
}
