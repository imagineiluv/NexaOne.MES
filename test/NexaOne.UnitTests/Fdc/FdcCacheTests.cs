using NexaOne.Common.Caching;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.UnitTests.Fdc;

/// <summary>
/// FDC 수집 핫패스 캐싱 회귀 안전망 — 태그 변경마다 반복되는 마스터/설정 조회를 캐시하고
/// 쓰기 경로에서 무효화하는지 검증한다(MdmMasterService 캐시 패턴 재사용).
/// 안전(STOP) 인터락 규칙은 응답성을 위해 의도적으로 캐시하지 않으므로(FdcInterlockService 미변경) 여기서 다루지 않는다.
/// 캐시는 선택적 생성자 인자라 미주입 시 직접 조회한다 — 이 테스트는 캐시를 주입한 경우의 동작을 고정한다.
/// </summary>
public sealed class FdcCacheTests
{
    private static FdcParameter ActiveParam(string id = "PARAM01") =>
        FdcParameter.Create(id, "Test Param", "EQ001", "℃", 0m, 100m).Value;

    // ── FdcDataService: 파라미터 한도 캐시 ──────────────────────────────────────

    [Fact]
    public async Task RecordData_caches_parameter_lookup_across_calls()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("PARAM01", default)).ReturnsAsync(ActiveParam());
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), default)).Returns(Task.CompletedTask);
        var svc = new FdcDataService(paramRepo.Object, dataRepo.Object, new MemoryCacheService());

        await svc.RecordDataAsync("CD001", "EQ001", "PARAM01", 55m, "Good");
        await svc.RecordDataAsync("CD002", "EQ001", "PARAM01", 60m, "Good");

        paramRepo.Verify(r => r.GetByIdAsync("PARAM01", default), Times.Once,
            "두 번째 수집은 캐시된 파라미터 한도를 써야 한다(DB 조회 1회)");
    }

    [Fact]
    public async Task AssignParameterToGroup_invalidates_parameter_cache()
    {
        var paramRepo = new Mock<IFdcParameterRepository>();
        var dataRepo  = new Mock<IFdcCollectDataRepository>();
        paramRepo.Setup(r => r.GetByIdAsync("PARAM01", default)).ReturnsAsync(ActiveParam());
        paramRepo.Setup(r => r.UpdateAsync(It.IsAny<FdcParameter>(), default)).Returns(Task.CompletedTask);
        dataRepo.Setup(r => r.AddAsync(It.IsAny<FdcCollectData>(), default)).Returns(Task.CompletedTask);
        var svc = new FdcDataService(paramRepo.Object, dataRepo.Object, new MemoryCacheService());

        await svc.RecordDataAsync("CD001", "EQ001", "PARAM01", 55m, "Good");  // 캐시 채움(조회 1)
        await svc.AssignParameterToGroupAsync("PARAM01", "G1");               // 직접 조회(2) + 무효화
        await svc.RecordDataAsync("CD002", "EQ001", "PARAM01", 60m, "Good");  // 무효화됨 → 재조회(3)

        paramRepo.Verify(r => r.GetByIdAsync("PARAM01", default), Times.Exactly(3),
            "그룹 배정 후 캐시가 무효화돼 다음 수집이 최신 파라미터를 다시 읽어야 한다(미무효화 시 2회에 그침)");
    }

    // ── FdcAlarmService: 활성 알람 설정 캐시 ─────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_caches_active_alarm_configs_across_calls()
    {
        var warn = FdcAlarmConfig.Create("AW", "EQ-001", "TEMP01", "Warning", "GT", 70m).Value;
        var cfgRepo = new Mock<IFdcAlarmConfigRepository>();
        cfgRepo.Setup(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { warn });
        var svc = new FdcAlarmService(cfgRepo.Object, historyRepository: null, cache: new MemoryCacheService());

        await svc.EvaluateAsync("EQ-001", "TEMP01", 95m);
        await svc.EvaluateAsync("EQ-001", "TEMP01", 96m);

        cfgRepo.Verify(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()), Times.Once,
            "두 번째 평가는 캐시된 활성 설정을 써야 한다(DB 조회 1회)");
    }

    [Fact]
    public async Task CreateConfig_invalidates_active_alarm_config_cache()
    {
        var warn = FdcAlarmConfig.Create("AW", "EQ-001", "TEMP01", "Warning", "GT", 70m).Value;
        var cfgRepo = new Mock<IFdcAlarmConfigRepository>();
        cfgRepo.Setup(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()))
               .ReturnsAsync(new[] { warn });
        cfgRepo.Setup(r => r.AddAsync(It.IsAny<FdcAlarmConfig>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var svc = new FdcAlarmService(cfgRepo.Object, historyRepository: null, cache: new MemoryCacheService());

        await svc.EvaluateAsync("EQ-001", "TEMP01", 95m);                                  // 캐시 채움(조회 1)
        await svc.CreateConfigAsync("AC", "EQ-001", "TEMP01", "Critical", "GT", 90m);      // 무효화
        await svc.EvaluateAsync("EQ-001", "TEMP01", 96m);                                  // 재조회(2)

        cfgRepo.Verify(r => r.GetActiveConfigsAsync("EQ-001", "TEMP01", It.IsAny<CancellationToken>()), Times.Exactly(2),
            "설정 생성 후 캐시가 무효화돼 다음 평가가 새 설정을 포함해 다시 읽어야 한다(미무효화 시 1회에 그침)");
    }
}
