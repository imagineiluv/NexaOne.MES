using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Controllers;
using NexaOne.SYS.Application.Deploys;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>§20.11 — DeployController: latest DTO 매핑(DownloadUrl), 업로드 검증,
/// 다운로드 Range 지원/파일명 응답을 검증한다.</summary>
public sealed class DeployControllerTests
{
    private static DeployFile Stored(string fileId, string version, bool isActive = true) =>
        DeployFile.Restore(fileId, version, $"NexaOne_{version}.zip", "hash123", 1024,
            "릴리스 노트", forceUpdate: true, isActive: isActive, "admin1", DateTime.UtcNow);

    private static (DeployController Controller, Mock<IDeployFileRepository> Repo, Mock<IDeployFileStorage> Storage)
        Build()
    {
        var repo = new Mock<IDeployFileRepository>();
        var storage = new Mock<IDeployFileStorage>();
        storage.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default))
            .ReturnsAsync(new StoredDeployFile("abc123", 1024));

        var controller = new DeployController(new DeployService(repo.Object, storage.Object))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "admin1") }, "test"))
                }
            }
        };
        return (controller, repo, storage);
    }

    // ── GetLatest ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatest_maps_download_url_and_release_note()
    {
        var (controller, repo, _) = Build();
        repo.Setup(r => r.GetActiveAsync(default))
            .ReturnsAsync(new List<DeployFile> { Stored("f1", "3.6.0.0") });

        var result = await controller.GetLatest(default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DeployLatestDto>().Subject;
        dto.Version.Should().Be("3.6.0.0");
        dto.DownloadUrl.Should().Be("/api/v1/deploy/files/f1/download",
            "클라이언트는 이 URL로 바로 다운로드한다");
        dto.Hash.Should().Be("hash123");
        dto.ForceUpdate.Should().BeTrue();
        dto.ReleaseNote.Should().Be("릴리스 노트");
    }

    [Fact]
    public async Task GetLatest_without_files_returns_404()
    {
        var (controller, repo, _) = Build();
        repo.Setup(r => r.GetActiveAsync(default)).ReturnsAsync(new List<DeployFile>());

        var result = await controller.GetLatest(default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_without_file_returns_400()
    {
        var (controller, repo, _) = Build();

        var result = await controller.Upload(null, "3.6.0.0", null, false, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        repo.Verify(r => r.InsertAsync(It.IsAny<DeployFile>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_valid_returns_dto_with_uploader_from_claims()
    {
        var (controller, repo, _) = Build();
        using var stream = new MemoryStream(new byte[32]);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "NexaOne_3.6.0.0.zip");

        var result = await controller.Upload(formFile, "3.6.0.0", "수정 사항", true, default);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DeployFileDto>().Subject;
        dto.Version.Should().Be("3.6.0.0");
        dto.FileName.Should().Be("NexaOne_3.6.0.0.zip");
        dto.UploadedBy.Should().Be("admin1", "업로더는 토큰 클레임에서 가져와야 한다");
        repo.Verify(r => r.InsertAsync(It.IsAny<DeployFile>(), default), Times.Once);
    }

    [Fact]
    public async Task Upload_file_over_limit_returns_400()
    {
        var (controller, repo, storage) = Build();
        using var stream = new MemoryStream(new byte[8]);
        // Length만 상한+1로 신고 — 컨트롤러는 스트림을 열기 전에 크기를 검사해야 한다
        var formFile = new FormFile(stream, 0, DeployController.MaxUploadBytes + 1, "file", "big.zip");

        var result = await controller.Upload(formFile, "3.6.0.0", null, false, default);

        result.Should().BeOfType<BadRequestObjectResult>("Kestrel 413 대신 사유 있는 400이어야 한다");
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
        repo.Verify(r => r.InsertAsync(It.IsAny<DeployFile>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_invalid_version_returns_400()
    {
        var (controller, _, storage) = Build();
        using var stream = new MemoryStream(new byte[32]);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "a.zip");

        var result = await controller.Upload(formFile, "v버전", null, false, default);

        result.Should().BeOfType<BadRequestObjectResult>();
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
    }

    // ── Download / Activate ───────────────────────────────────────────────────

    [Fact]
    public async Task Download_returns_octet_stream_with_range_support()
    {
        var (controller, repo, storage) = Build();
        var binary = new MemoryStream(new byte[8]);
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(Stored("f1", "3.6.0.0"));
        storage.Setup(s => s.OpenRead("f1")).Returns(binary);

        var result = await controller.Download("f1", default);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("application/octet-stream");
        file.FileDownloadName.Should().Be("NexaOne_3.6.0.0.zip");
        file.EnableRangeProcessing.Should().BeTrue("대용량 패키지 이어받기(HTTP Range)를 지원해야 한다");
    }

    [Fact]
    public async Task Download_inactive_returns_404()
    {
        var (controller, repo, _) = Build();
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(Stored("f1", "3.6.0.0", isActive: false));

        var result = await controller.Download("f1", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Deactivate_unknown_file_returns_404()
    {
        var (controller, repo, _) = Build();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((DeployFile?)null);

        var result = await controller.Deactivate("ghost", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Activate_known_file_returns_204_and_updates()
    {
        var (controller, repo, _) = Build();
        var file = Stored("f1", "3.6.0.0", isActive: false);
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(file);

        var result = await controller.Activate("f1", default);

        result.Should().BeOfType<NoContentResult>();
        file.IsActive.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(file, default), Times.Once);
    }
}
