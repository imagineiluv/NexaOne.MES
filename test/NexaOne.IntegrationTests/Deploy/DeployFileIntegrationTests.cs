using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace NexaOne.IntegrationTests.Deploy;

/// <summary>
/// DeployController(§20.11) HTTP 통합 테스트 — 마이그레이션 V012로 생성되는 SYS_DEPLOY_FILE 테이블과
/// 파일시스템 바이너리 저장소(IDeployFileStorage = FileSystemDeployFileStorage)가 SQLite + 인프로세스
/// 호스트에서 실제로 동작하는지(테이블 존재 + 방언 SELECT/INSERT/UPDATE + 인증/인가 + multipart 모델
/// 바인딩 + 바이너리 왕복 + 영속)를 end-to-end로 검증한다. Deploy 영역 통합테스트는 전무했다(신규 파일).
///
/// 시나리오: 업로드(POST files, multipart) → latest/목록 회수(GET) → 다운로드 바이트 동일성(GET download)
///           → 비활성화(PUT deactivate)로 latest/다운로드 제외 → 활성화(PUT activate) 복귀.
///
/// 인가: POST files / GET files / PUT (de)activate는 perm:deploy:manage 정책을 요구한다.
///       기본 인증 클라이언트는 ADMIN "*"(전체 권한) 토큰(sub="test-admin")을 발급하므로 정책을 통과한다.
///       GET latest / GET download는 [AllowAnonymous](로그인 전 클라이언트 자동 업데이트 경로).
///
/// 비-부인성: UploadedBy는 요청 본문이 아니라 토큰 클레임(CurrentUserId → sub="test-admin")에서 기록되는지
///            업로드 응답/목록 되읽기로 검증한다(DeployController.CurrentUserId 사용).
///
/// 저장소: FileSystemDeployFileStorage는 Deploy:StoragePath 미설정 시 AppContext.BaseDirectory/App_Data/Deploy
///         (테스트 출력 폴더 하위)에 fileId.bin으로 쓴다 — 추가 설정 없이 동작한다. 단, 이 저장소는 싱글톤이라
///         테스트 클래스 간 디렉터리를 공유하나 fileId가 GUID("N")라 충돌하지 않는다(DB는 클래스별 격리).
///
/// FK 비강제(하니스 PRAGMA foreign_keys=OFF)와 무관 — SYS_DEPLOY_FILE은 교차모듈 FK 부모가 없다.
/// 모든 전이 대상 행은 본 테스트가 먼저 업로드해 생성하므로 선행 시드가 불필요하다.
/// </summary>
public sealed class DeployFileIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public DeployFileIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 미인증 차단 (POST files — perm:deploy:manage) ────────────────────────────

    [Fact]
    public async Task UploadFile_requires_auth()
    {
        // 미인증 → 401: [Authorize] + perm:deploy:manage가 multipart 파싱/모델바인딩보다 먼저 적용되는지 검증.
        var anon = _factory.CreateClient();
        using var form = BuildUploadForm(new byte[] { 1, 2, 3 }, "3.9.0.0", "anon", forceUpdate: false, "anon.bin");

        var resp = await anon.PostAsync("/api/v1/deploy/files", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "POST deploy/files는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task GetFiles_requires_auth()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/deploy/files")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET deploy/files는 토큰 없이 401이어야 한다");
    }

    // ── 업로드 → 목록/latest → 다운로드 왕복 (해피패스) ──────────────────────────

    [Fact]
    public async Task Upload_then_download_roundtrips_binary_and_records_uploader_from_token()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN, sub="test-admin") — perm:deploy:manage 통과
        const string version = "4.1.0.0";
        const string fileName = "nexaone-setup.bin";
        // 작은(비-텍스트) 바이트 패킷 — 다운로드 응답과 바이트 단위 동일성으로 저장/스트림 왕복을 실증한다.
        var payload = new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF, 0x10, 0x20, 0x7F, 0x80 };

        // 1) 업로드 — multipart/form-data. SYS_DEPLOY_FILE INSERT + 디스크 SaveAsync(SHA-256 계산).
        using var form = BuildUploadForm(payload, version, "first release", forceUpdate: true, fileName);
        var uploadResp = await client.PostAsync("/api/v1/deploy/files", form);
        var uploadBody = await uploadResp.Content.ReadAsStringAsync();
        uploadResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"파일 업로드(SYS_DEPLOY_FILE INSERT + 디스크 저장)가 성공해야 한다. 응답 본문: {uploadBody}");

        var uploaded = await uploadResp.Content.ReadFromJsonAsync<DeployFileDto>();
        uploaded.Should().NotBeNull("업로드 응답은 등록된 배포 파일 메타(Result.Value)여야 한다");
        uploaded!.Version.Should().Be(version);
        uploaded.FileName.Should().Be(fileName);
        uploaded.FileSize.Should().Be(payload.Length, "FILE_SIZE는 업로드 바이트 수여야 한다");
        uploaded.ForceUpdate.Should().BeTrue("forceUpdate 폼 필드가 true로 바인딩돼야 한다");
        uploaded.IsActive.Should().BeTrue("신규 업로드는 활성 상태로 생성된다");
        uploaded.Description.Should().Be("first release");
        // 비-부인성: 업로더는 본문이 아니라 토큰(sub="test-admin")에서 기록돼야 한다.
        uploaded.UploadedBy.Should().Be("test-admin",
            "UploadedBy는 요청 본문이 아니라 인증 토큰의 사용자(CurrentUserId)에서 와야 한다");
        // 해시는 서버가 계산한 SHA-256 hex(소문자). 클라이언트 측 기대값과 일치해야 무결성 검증이 가능하다.
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        uploaded.Hash.Should().Be(expectedHash, "HASH는 업로드 내용의 SHA-256 hex(소문자)여야 한다");

        var fileId = uploaded.FileId;
        fileId.Should().NotBeNullOrWhiteSpace();

        // 2) 목록 회수(GET files) — SYS_DEPLOY_FILE SELECT(IS_ACTIVE INTEGER→bool 매핑 포함)로 영속화 확인.
        var list = await client.GetFromJsonAsync<List<DeployFileDto>>("/api/v1/deploy/files");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(f => f.FileId == fileId)
            .Which.Version.Should().Be(version);

        // 3) latest 회수(GET latest, 익명) — 활성 최신 버전 선정 + 다운로드 URL 매핑.
        //    (같은 클래스 DB를 공유하는 다른 테스트가 더 높은 버전을 활성으로 남겼을 수 있어, latest가 반드시
        //     이 버전이라고 단언하지 않는다. 200 + 잘 형성된 download URL/Hash 매핑만 검증한다 — 순서 무관.)
        var anon = _factory.CreateClient();   // latest/download는 AllowAnonymous
        var latestResp = await anon.GetAsync("/api/v1/deploy/latest");
        var latestBody = await latestResp.Content.ReadAsStringAsync();
        latestResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET latest는 활성 최신 버전을 200으로 반환해야 한다. 응답 본문: {latestBody}");
        var latest = await latestResp.Content.ReadFromJsonAsync<DeployLatestDto>();
        latest.Should().NotBeNull();
        latest!.DownloadUrl.Should().StartWith("/api/v1/deploy/files/",
            "DownloadUrl은 단건 다운로드 경로(/api/v1/deploy/files/{id}/download) 형식이어야 한다");
        latest.DownloadUrl.Should().EndWith("/download");
        latest.Hash.Should().NotBeNullOrWhiteSpace("latest 응답은 무결성 검증용 HASH를 포함해야 한다");

        // 4) 다운로드(GET download, 익명) — 본 테스트가 올린 파일을 fileId로 직접 받아 업로드 바이트와
        //    정확히 동일한지 검증한다(디스크 OpenRead 스트림 왕복). 순서 의존 없이 이 파일을 콕 집어 받는다.
        var downloadResp = await anon.GetAsync($"/api/v1/deploy/files/{fileId}/download");
        downloadResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "활성 파일 다운로드는 200이어야 한다");
        downloadResp.Content.Headers.ContentType?.MediaType
            .Should().Be("application/octet-stream", "바이너리 다운로드는 octet-stream이어야 한다");
        var downloaded = await downloadResp.Content.ReadAsByteArrayAsync();
        downloaded.Should().Equal(payload, "다운로드 바이트는 업로드 바이트와 1:1 동일해야 한다(저장/스트림 왕복)");
        // 다운로드 응답으로 무결성 재검증: 받은 바이트의 SHA-256이 업로드 시 기록한 HASH와 같아야 한다.
        Convert.ToHexString(SHA256.HashData(downloaded)).ToLowerInvariant()
            .Should().Be(expectedHash, "다운로드 내용의 해시가 등록 HASH와 일치해야 한다");
    }

    // ── 상태전이: deactivate(회수) → latest/다운로드 제외 → activate(복귀) ────────

    [Fact]
    public async Task Deactivate_excludes_from_latest_and_download_then_activate_restores()
    {
        var client = _factory.CreateAuthenticatedClient();   // perm:deploy:manage 통과
        const string version = "4.2.0.0";
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

        // 0) 회수 대상 버전 업로드(전이 대상 행 선생성 — FK-OFF와 무관하게 성립).
        using var form = BuildUploadForm(payload, version, "to be recalled", forceUpdate: false, "recall.bin");
        var uploadResp = await client.PostAsync("/api/v1/deploy/files", form);
        var uploadBody = await uploadResp.Content.ReadAsStringAsync();
        uploadResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"회수 시나리오 선행 업로드가 성공해야 한다. 응답 본문: {uploadBody}");
        var fileId = (await uploadResp.Content.ReadFromJsonAsync<DeployFileDto>())!.FileId;

        var anon = _factory.CreateClient();   // latest/download는 AllowAnonymous

        // 사전 확인: 활성 상태에선 다운로드가 가능하다.
        (await anon.GetAsync($"/api/v1/deploy/files/{fileId}/download")).StatusCode
            .Should().Be(HttpStatusCode.OK, "활성 파일은 다운로드 가능해야 한다");

        // 1) 비활성화(PUT deactivate) — SYS_DEPLOY_FILE UPDATE SET IS_ACTIVE=0. 본문 없음, 204 기대.
        var deactivateResp = await client.PutAsync($"/api/v1/deploy/files/{fileId}/deactivate", content: null);
        var deactivateBody = await deactivateResp.Content.ReadAsStringAsync();
        deactivateResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"비활성화(UPDATE IS_ACTIVE=0)는 204여야 한다. 응답 본문: {deactivateBody}");

        // 1-1) 되읽기로 UPDATE 영속화 검증 — 목록에서 IsActive=false로 회수돼야 한다(읽기경로 상태손실 회귀).
        var afterDeactivate = await client.GetFromJsonAsync<List<DeployFileDto>>("/api/v1/deploy/files");
        afterDeactivate!.Should().ContainSingle(f => f.FileId == fileId)
            .Which.IsActive.Should().BeFalse("비활성화가 SQLite에 영속화되고 되읽기로 false로 회수돼야 한다");

        // 1-2) 비활성은 다운로드에서도 제외(404) — OpenDownloadAsync가 !IsActive를 NotFound로 막는다.
        (await anon.GetAsync($"/api/v1/deploy/files/{fileId}/download")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "비활성 파일은 다운로드가 차단(404)돼야 한다");

        // 1-3) 비활성은 latest 선정에서도 제외. (같은 클래스 DB를 공유하는 다른 테스트가 활성 버전을 남겼을 수
        //      있어 latest가 404가 아닐 수 있으므로, '회수된 이 버전이 latest로 선정되지 않음'만 단언한다 —
        //      테스트 실행 순서에 의존하지 않는다.)
        var latestAfterDeactivate = await anon.GetAsync("/api/v1/deploy/latest");
        if (latestAfterDeactivate.StatusCode == HttpStatusCode.OK)
        {
            var picked = await latestAfterDeactivate.Content.ReadFromJsonAsync<DeployLatestDto>();
            picked!.Version.Should().NotBe(version,
                "비활성화된 버전은 latest 선정에서 제외돼야 한다");
        }

        // 2) 활성화(PUT activate) — SYS_DEPLOY_FILE UPDATE SET IS_ACTIVE=1로 복귀.
        var activateResp = await client.PutAsync($"/api/v1/deploy/files/{fileId}/activate", content: null);
        var activateBody = await activateResp.Content.ReadAsStringAsync();
        activateResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"활성화(UPDATE IS_ACTIVE=1)는 204여야 한다. 응답 본문: {activateBody}");

        // 2-1) 되읽기로 복귀 확인 — IsActive=true.
        var afterActivate = await client.GetFromJsonAsync<List<DeployFileDto>>("/api/v1/deploy/files");
        afterActivate!.Should().ContainSingle(f => f.FileId == fileId)
            .Which.IsActive.Should().BeTrue("활성화 복귀가 되읽기로 true로 회수돼야 한다");

        // 2-2) latest/다운로드 복귀 — 다시 최신으로 선정되고 다운로드 가능해야 한다.
        var latest = await anon.GetFromJsonAsync<DeployLatestDto>("/api/v1/deploy/latest");
        latest.Should().NotBeNull();
        latest!.Version.Should().Be(version, "활성화 복귀 후 latest로 다시 선정돼야 한다");

        var redownloaded = await anon.GetByteArrayAsync($"/api/v1/deploy/files/{fileId}/download");
        redownloaded.Should().Equal(payload, "활성화 복귀 후 다운로드 바이트가 원본과 동일해야 한다");
    }

    // ── multipart 폼 빌더 ────────────────────────────────────────────────────────

    /// <summary>
    /// Upload 액션의 시그니처(IFormFile? file, [FromForm] string? version/description, bool forceUpdate)에
    /// 맞춰 multipart/form-data를 구성한다. 파일 파트 이름은 컨트롤러 파라미터명 "file"과 정확히 일치해야
    /// 모델바인딩된다. forceUpdate는 폼 필드 문자열("true"/"false")로 보낸다.
    /// </summary>
    private static MultipartFormDataContent BuildUploadForm(
        byte[] bytes, string version, string description, bool forceUpdate, string fileName)
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { fileContent, "file", fileName },
            { new StringContent(version), "version" },
            { new StringContent(description), "description" },
            { new StringContent(forceUpdate ? "true" : "false"), "forceUpdate" },
        };
    }

    // ── DTOs — 컨트롤러 응답 record와 1:1 정합(JSON camelCase). ───────────────────
    // DeployFileDto(FileId, Version, FileName, Hash, FileSize, Description, ForceUpdate, IsActive, UploadedBy, UploadedAt)
    private sealed record DeployFileDto(
        string FileId, string Version, string FileName, string Hash, long FileSize,
        string Description, bool ForceUpdate, bool IsActive, string UploadedBy, DateTime UploadedAt);

    // DeployLatestDto(Version, DownloadUrl, Hash, ForceUpdate, ReleaseNote)
    private sealed record DeployLatestDto(
        string Version, string DownloadUrl, string Hash, bool ForceUpdate, string ReleaseNote);
}
