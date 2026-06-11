using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Deploys;

/// <summary>
/// 배포 파일 업로드 / 클라이언트 자동 업데이트 (설계서 20.11).
/// - 메타데이터는 SYS_DEPLOY_FILE, 바이너리는 IDeployFileStorage(서버 디스크)에 보관한다.
/// - 최신 버전 선정은 문자열 정렬이 아닌 System.Version 비교로 한다 ('1.10.0' &gt; '1.9.0' 보장).
/// - 문제 버전은 비활성화로 회수한다 — latest 선정/다운로드에서 제외.
/// </summary>
public sealed class DeployService
{
    /// <summary>VERSION 컬럼(NVARCHAR(20))에 맞춘 버전 문자열 최대 길이.</summary>
    public const int MaxVersionLength = 20;

    /// <summary>FILE_NAME 컬럼(NVARCHAR(260))에 맞춘 파일명 최대 길이.</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>DESCRIPTION 컬럼(NVARCHAR(500))에 맞춘 릴리스 노트 최대 길이.</summary>
    public const int MaxDescriptionLength = 500;

    private readonly IDeployFileRepository _repository;
    private readonly IDeployFileStorage _storage;

    public DeployService(IDeployFileRepository repository, IDeployFileStorage storage)
    {
        _repository = repository;
        _storage = storage;
    }

    public async Task<Result<DeployFile>> UploadAsync(
        string version,
        string fileName,
        string? description,
        bool forceUpdate,
        Stream content,
        string uploadedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uploadedBy))
            return Result.Failure<DeployFile>(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(version))
            return Result.Failure<DeployFile>(Error.Validation("버전을 입력해 주세요."));

        version = version.Trim();
        if (version.Length > MaxVersionLength)
            return Result.Failure<DeployFile>(Error.Validation($"버전은 {MaxVersionLength}자 이하여야 합니다."));
        // System.Version 파싱 가능 형식만 허용 — latest 선정(버전 비교)이 깨지지 않게 입력 단계에서 보장한다
        if (!Version.TryParse(version, out _))
            return Result.Failure<DeployFile>(Error.Validation("버전은 '3.5.1.0' 같은 숫자 버전 형식이어야 합니다."));

        if (string.IsNullOrWhiteSpace(fileName))
            return Result.Failure<DeployFile>(Error.Validation("파일명을 확인해 주세요."));
        fileName = fileName.Trim();
        // 업로드 파일명은 다운로드 응답(Content-Disposition)에 그대로 쓰인다 — 경로 성분을 차단한다
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            return Result.Failure<DeployFile>(Error.Validation("파일명에 경로를 포함할 수 없습니다."));
        // CR/LF 등 제어 문자가 응답 헤더에 실리면 해당 버전 다운로드가 영구 실패한다 — 입력 단계에서 차단
        if (fileName.Any(char.IsControl))
            return Result.Failure<DeployFile>(Error.Validation("파일명에 제어 문자를 포함할 수 없습니다."));
        if (fileName.Length > MaxFileNameLength)
            return Result.Failure<DeployFile>(Error.Validation($"파일명은 {MaxFileNameLength}자 이하여야 합니다."));

        var releaseNote = (description ?? string.Empty).Trim();
        if (releaseNote.Length > MaxDescriptionLength)
            return Result.Failure<DeployFile>(Error.Validation($"설명은 {MaxDescriptionLength}자 이하여야 합니다."));

        if (content is null)
            return Result.Failure<DeployFile>(Error.Validation("업로드할 파일이 없습니다."));

        // 동시 업로드 race는 UQ_SYS_DEPLOY_FILE_VERSION(UNIQUE)이 최종 방어한다 — 여기서는 친절한 사전 검사
        if (await _repository.GetByVersionAsync(version, ct) is not null)
            return Result.Failure<DeployFile>(Error.Conflict($"이미 등록된 버전입니다: {version}"));

        var fileId = Guid.NewGuid().ToString("N");
        StoredDeployFile stored;
        try
        {
            stored = await _storage.SaveAsync(fileId, content, ct);
        }
        catch
        {
            // 전송 중단(클라이언트 끊김, 네트워크 오류)으로 부분 기록된 바이너리를 정리하고 원인 예외를 올린다
            _storage.Delete(fileId);
            throw;
        }
        if (stored.Size <= 0)
        {
            _storage.Delete(fileId);
            return Result.Failure<DeployFile>(Error.Validation("업로드할 파일이 비어 있습니다."));
        }

        try
        {
            var file = DeployFile.Create(
                fileId, version, fileName, stored.Hash, stored.Size, releaseNote, forceUpdate, uploadedBy);
            await _repository.InsertAsync(file, ct);
            return Result.Success(file);
        }
        catch
        {
            // 메타 기록 실패(UNIQUE 충돌 포함) 시 고아 바이너리를 정리하고 원인 예외를 그대로 올린다
            _storage.Delete(fileId);
            throw;
        }
    }

    /// <summary>활성 버전 중 최신 — 클라이언트가 기동 시 버전 비교에 사용한다.</summary>
    public async Task<Result<DeployFile>> GetLatestAsync(CancellationToken ct = default)
    {
        var actives = await _repository.GetActiveAsync(ct);
        var latest = actives
            .Select(f => (File: f, Parsed: Version.TryParse(f.Version, out var v) ? v : null))
            .Where(x => x.Parsed is not null)   // 파싱 불가 행(직접 INSERT 등)은 선정에서 제외
            .OrderByDescending(x => x.Parsed)
            .Select(x => x.File)
            .FirstOrDefault();

        return latest is null
            ? Result.Failure<DeployFile>(Error.NotFound("배포된 파일이 없습니다."))
            : Result.Success(latest);
    }

    public async Task<Result<IReadOnlyList<DeployFile>>> GetAllAsync(CancellationToken ct = default)
    {
        var files = await _repository.GetAllAsync(ct);
        return Result.Success(files);
    }

    /// <summary>버전 활성/비활성 전환 — 비활성은 latest 선정/다운로드에서 제외된다 (문제 버전 회수).</summary>
    public async Task<Result> SetActiveAsync(string fileId, bool isActive, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return Result.Failure(Error.Validation("FileId is required."));

        var file = await _repository.GetByIdAsync(fileId.Trim(), ct);
        if (file is null)
            return Result.Failure(Error.NotFound("배포 파일을 찾을 수 없습니다."));

        if (isActive) file.Activate();
        else file.Deactivate();
        await _repository.UpdateAsync(file, ct);
        return Result.Success();
    }

    /// <summary>다운로드 스트림 열기 — 비활성 버전은 다운로드도 차단한다.</summary>
    public async Task<Result<DeployDownload>> OpenDownloadAsync(string fileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return Result.Failure<DeployDownload>(Error.Validation("FileId is required."));

        var file = await _repository.GetByIdAsync(fileId.Trim(), ct);
        if (file is null || !file.IsActive)
            return Result.Failure<DeployDownload>(Error.NotFound("배포 파일을 찾을 수 없습니다."));

        var stream = _storage.OpenRead(file.FileId);
        if (stream is null)
            return Result.Failure<DeployDownload>(
                Error.Failure("배포 파일 저장소에서 파일을 찾을 수 없습니다. 관리자에게 문의해 주세요."));

        return Result.Success(new DeployDownload(file, stream));
    }
}

/// <summary>다운로드 응답 재료 — 메타데이터와 열린 읽기 스트림 (스트림 폐기는 호출자 책임).</summary>
public sealed record DeployDownload(DeployFile File, Stream Content);
