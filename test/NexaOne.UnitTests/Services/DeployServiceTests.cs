using NexaOne.SYS.Application.Deploys;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Services;

/// <summary>§20.11 — 배포 파일 업로드/최신 선정/회수: 버전 검증, 고아 바이너리 정리,
/// System.Version 비교(문자열 정렬 함정 회피)를 검증한다.</summary>
public sealed class DeployServiceTests
{
    private static DeployFile Stored(string fileId, string version, bool isActive = true) =>
        DeployFile.Restore(fileId, version, $"NexaOne_{version}.zip", "hash", 1024,
            "", forceUpdate: false, isActive: isActive, "admin1", DateTime.UtcNow);

    private static (DeployService Service, Mock<IDeployFileRepository> Repo, Mock<IDeployFileStorage> Storage)
        Build(long storedSize = 1024, string storedHash = "abc123")
    {
        var repo = new Mock<IDeployFileRepository>();
        var storage = new Mock<IDeployFileStorage>();
        storage.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default))
            .ReturnsAsync(new StoredDeployFile(storedHash, storedSize));
        return (new DeployService(repo.Object, storage.Object), repo, storage);
    }

    // ── UploadAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_valid_saves_binary_and_inserts_metadata()
    {
        var (service, repo, storage) = Build(storedSize: 2048, storedHash: "deadbeef");
        using var content = new MemoryStream(new byte[16]);

        var result = await service.UploadAsync(
            " 3.6.0.0 ", "NexaOne_3.6.0.0.zip", "버그 수정", forceUpdate: true, content, "admin1");

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("3.6.0.0", "버전은 Trim 후 저장돼야 한다");
        result.Value.Hash.Should().Be("deadbeef");
        result.Value.FileSize.Should().Be(2048);
        result.Value.IsActive.Should().BeTrue("신규 업로드는 즉시 배포 대상이어야 한다");
        result.Value.ForceUpdate.Should().BeTrue();
        repo.Verify(r => r.InsertAsync(result.Value, default), Times.Once);
        storage.Verify(s => s.Delete(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("v3.6.0")]        // 접두사 불가
    [InlineData("3.6.0-beta")]    // 시맨틱 접미사 불가
    [InlineData("최신")]
    public async Task Upload_unparsable_version_fails_before_storage(string version)
    {
        var (service, repo, storage) = Build();
        using var content = new MemoryStream(new byte[16]);

        var result = await service.UploadAsync(version, "a.zip", null, false, content, "admin1");

        result.IsFailure.Should().BeTrue("System.Version 비교가 깨지는 입력은 차단돼야 한다");
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
        repo.Verify(r => r.InsertAsync(It.IsAny<DeployFile>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_version_over_column_length_fails()
    {
        var (service, _, storage) = Build();
        using var content = new MemoryStream(new byte[16]);

        // 21자 — VERSION NVARCHAR(20) 초과
        var result = await service.UploadAsync("111111.222222.333333.", "a.zip", null, false, content, "admin1");

        result.IsFailure.Should().BeTrue();
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
    }

    [Theory]
    [InlineData(@"..\evil.exe")]
    [InlineData("dir/evil.exe")]
    public async Task Upload_filename_with_path_segment_fails(string fileName)
    {
        var (service, _, storage) = Build();
        using var content = new MemoryStream(new byte[16]);

        var result = await service.UploadAsync("3.6.0.0", fileName, null, false, content, "admin1");

        result.IsFailure.Should().BeTrue("파일명은 다운로드 응답 헤더에 그대로 쓰이므로 경로 성분을 차단해야 한다");
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_duplicate_version_returns_conflict()
    {
        var (service, repo, storage) = Build();
        repo.Setup(r => r.GetByVersionAsync("3.6.0.0", default)).ReturnsAsync(Stored("f1", "3.6.0.0"));
        using var content = new MemoryStream(new byte[16]);

        var result = await service.UploadAsync("3.6.0.0", "a.zip", null, false, content, "admin1");

        result.IsFailure.Should().BeTrue();
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_empty_stream_deletes_orphan_binary()
    {
        var (service, repo, storage) = Build(storedSize: 0);
        using var content = new MemoryStream();

        var result = await service.UploadAsync("3.6.0.0", "a.zip", null, false, content, "admin1");

        result.IsFailure.Should().BeTrue();
        storage.Verify(s => s.Delete(It.IsAny<string>()), Times.Once, "빈 파일은 저장소에 남기면 안 된다");
        repo.Verify(r => r.InsertAsync(It.IsAny<DeployFile>(), default), Times.Never);
    }

    [Theory]
    [InlineData("evil\r\n.zip")]   // 응답 헤더 인젝션/다운로드 실패 유발
    [InlineData("evil\t.zip")]
    public async Task Upload_filename_with_control_char_fails(string fileName)
    {
        var (service, _, storage) = Build();
        using var content = new MemoryStream(new byte[16]);

        var result = await service.UploadAsync("3.6.0.0", fileName, null, false, content, "admin1");

        result.IsFailure.Should().BeTrue("제어 문자가 Content-Disposition에 실리면 해당 버전 다운로드가 영구 실패한다");
        storage.Verify(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_save_failure_cleans_up_partial_binary_and_rethrows()
    {
        var (service, repo, storage) = Build();
        // 대용량 전송 중 클라이언트 끊김 — 스트리밍 저장이 중간에 실패하는 시나리오
        storage.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), default))
            .ThrowsAsync(new IOException("connection reset"));
        using var content = new MemoryStream(new byte[16]);

        var act = () => service.UploadAsync("3.6.0.0", "a.zip", null, false, content, "admin1");

        await act.Should().ThrowAsync<IOException>();
        storage.Verify(s => s.Delete(It.IsAny<string>()), Times.Once, "부분 기록된 바이너리는 정리돼야 한다");
        repo.Verify(r => r.InsertAsync(It.IsAny<DeployFile>(), default), Times.Never);
    }

    [Fact]
    public async Task Upload_insert_failure_cleans_up_binary_and_rethrows()
    {
        var (service, repo, storage) = Build();
        // UNIQUE 제약 충돌 등 메타 기록 실패 시나리오
        repo.Setup(r => r.InsertAsync(It.IsAny<DeployFile>(), default))
            .ThrowsAsync(new InvalidOperationException("UNIQUE constraint"));
        using var content = new MemoryStream(new byte[16]);

        var act = () => service.UploadAsync("3.6.0.0", "a.zip", null, false, content, "admin1");

        await act.Should().ThrowAsync<InvalidOperationException>();
        storage.Verify(s => s.Delete(It.IsAny<string>()), Times.Once, "메타 없는 고아 바이너리는 정리돼야 한다");
    }

    // ── GetLatestAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatest_compares_as_version_not_string()
    {
        var (service, repo, _) = Build();
        repo.Setup(r => r.GetActiveAsync(default)).ReturnsAsync(new List<DeployFile>
        {
            Stored("f1", "1.9.0"),
            Stored("f2", "1.10.0"),   // 문자열 정렬이면 "1.9.0"이 더 크다
        });

        var result = await service.GetLatestAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("1.10.0", "버전 비교는 숫자 세그먼트 기준이어야 한다");
    }

    [Fact]
    public async Task GetLatest_skips_unparsable_rows()
    {
        var (service, repo, _) = Build();
        repo.Setup(r => r.GetActiveAsync(default)).ReturnsAsync(new List<DeployFile>
        {
            Stored("f1", "직접입력행"),   // DB 직접 INSERT 등 비정상 행
            Stored("f2", "2.0.0"),
        });

        var result = await service.GetLatestAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be("2.0.0");
    }

    [Fact]
    public async Task GetLatest_without_active_files_returns_not_found()
    {
        var (service, repo, _) = Build();
        repo.Setup(r => r.GetActiveAsync(default)).ReturnsAsync(new List<DeployFile>());

        var result = await service.GetLatestAsync();

        result.IsFailure.Should().BeTrue();
    }

    // ── SetActiveAsync / OpenDownloadAsync ────────────────────────────────────

    [Fact]
    public async Task SetActive_unknown_file_returns_not_found()
    {
        var (service, repo, _) = Build();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((DeployFile?)null);

        var result = await service.SetActiveAsync("ghost", isActive: false);

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<DeployFile>(), default), Times.Never);
    }

    [Fact]
    public async Task SetActive_false_deactivates_and_updates()
    {
        var (service, repo, _) = Build();
        var file = Stored("f1", "3.6.0.0");
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(file);

        var result = await service.SetActiveAsync("f1", isActive: false);

        result.IsSuccess.Should().BeTrue();
        file.IsActive.Should().BeFalse("문제 버전 회수는 비활성화로 표현된다");
        repo.Verify(r => r.UpdateAsync(file, default), Times.Once);
    }

    [Fact]
    public async Task OpenDownload_inactive_file_returns_not_found()
    {
        var (service, repo, storage) = Build();
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(Stored("f1", "3.6.0.0", isActive: false));

        var result = await service.OpenDownloadAsync("f1");

        result.IsFailure.Should().BeTrue("회수된 버전은 다운로드도 차단돼야 한다");
        storage.Verify(s => s.OpenRead(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OpenDownload_missing_binary_returns_failure()
    {
        var (service, repo, storage) = Build();
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(Stored("f1", "3.6.0.0"));
        storage.Setup(s => s.OpenRead("f1")).Returns((Stream?)null);

        var result = await service.OpenDownloadAsync("f1");

        result.IsFailure.Should().BeTrue("메타는 있는데 바이너리가 없으면 명시적 오류여야 한다");
    }

    [Fact]
    public async Task OpenDownload_active_file_returns_metadata_and_stream()
    {
        var (service, repo, storage) = Build();
        var file = Stored("f1", "3.6.0.0");
        using var binary = new MemoryStream(new byte[8]);
        repo.Setup(r => r.GetByIdAsync("f1", default)).ReturnsAsync(file);
        storage.Setup(s => s.OpenRead("f1")).Returns(binary);

        var result = await service.OpenDownloadAsync("f1");

        result.IsSuccess.Should().BeTrue();
        result.Value.File.Should().BeSameAs(file);
        result.Value.Content.Should().BeSameAs(binary);
    }
}
