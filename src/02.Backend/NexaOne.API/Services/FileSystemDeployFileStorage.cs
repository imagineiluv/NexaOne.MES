using System.Security.Cryptography;
using NexaOne.SYS.Application.Deploys;

namespace NexaOne.API.Services;

/// <summary>
/// IDeployFileStorage 파일시스템 구현 (설계서 20.11).
/// 현행 서버 공유 폴더의 웹 적응 — API 서버 디스크(Deploy:StoragePath)에 fileId 키로 보관한다.
/// </summary>
public sealed class FileSystemDeployFileStorage : IDeployFileStorage
{
    private const int BufferSize = 81920;

    private readonly string _rootPath;

    public FileSystemDeployFileStorage(string rootPath)
    {
        _rootPath = rootPath;
        Directory.CreateDirectory(rootPath);
    }

    public async Task<StoredDeployFile> SaveAsync(string fileId, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(fileId);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;

        await using (var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            // 한 번 읽은 청크로 해시와 디스크 쓰기를 같이 처리 — 대용량 패키지도 메모리에 올리지 않는다
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }
        }

        return new StoredDeployFile(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), total);
    }

    public Stream? OpenRead(string fileId)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path)) return null;
        // Read 공유 — 같은 파일을 여러 클라이언트가 동시에 내려받을 수 있어야 한다
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
    }

    public void Delete(string fileId)
    {
        var path = PathFor(fileId);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string fileId)
    {
        // 정상 경로에선 서비스가 생성한 GUID("N")만 들어오지만,
        // DB 행 변조 등으로 임의 값이 와도 경로 탐색이 되지 않게 영숫자만 허용한다
        if (string.IsNullOrEmpty(fileId) || !fileId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Invalid deploy file id.", nameof(fileId));
        return Path.Combine(_rootPath, fileId + ".bin");
    }
}
