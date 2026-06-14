using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.MsSql;
using NexaOne.API.Services;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Application.Messaging;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Infrastructure;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Infrastructure;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Infrastructure;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Infrastructure;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Infrastructure;
using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Infrastructure;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Application.Deploys;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Infrastructure;

namespace NexaOne.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexaOneServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString("NexaOne")
            ?? throw new InvalidOperationException("ConnectionStrings:NexaOne is required");

        // DB 공급자 선택(런타임): Database:Provider = "MsSql"(기본) | "Sqlite"(테스트/로컬).
        // MsSqlProvider는 자체적으로 INexaOneEESDbCapability를 구현하나, SQLite는 별도 어댑터로 분리한다.
        var dbProvider = configuration.GetValue<string>("Database:Provider") ?? "MsSql";
        IDatabaseProvider provider;
        INexaOneEESDbCapability capability;
        if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            provider = new NexusCom.Data.Sqlite.SqliteProvider();
            capability = new SqliteEesDbCapability();
        }
        else
        {
            var mssql = new MsSqlProvider();
            provider = mssql;
            capability = mssql;
        }

        var dataSource = new EesDataSource
        {
            Provider = provider,
            ConnectionString = connStr
        };

        services.AddSingleton(provider);
        services.AddSingleton(capability);
        services.AddSingleton(dataSource);
        // 공급자의 트랜잭션 관리자를 노출(SqlTxnContext 의존성) — 부팅 시 DI 검증 통과.
        services.AddSingleton<ITransactionManager>(provider.TransactionManager);

        services.AddScoped<SqlTxnContext>();
        services.AddScoped<QueryRepository>();
        services.AddScoped(sp =>
            new ServiceObjectProcessor(
                sp.GetRequiredService<EesDataSource>(),
                sp.GetService<IHttpContextAccessor>()?.HttpContext?.User?.Identity?.Name ?? "SYSTEM"));

        services.AddHttpContextAccessor();

        // 캐시(ICacheService) — 읽기 빈도 높은 마스터/조회 캐시. 기본 인메모리(IMemoryCache),
        // Cache:Provider=Redis면 분산 캐시(RedisDriver, opt-in 인프라)로 전환한다.
        services.AddMemoryCache();
        var cacheProvider = configuration.GetValue<string>("Cache:Provider") ?? "Memory";
        if (string.Equals(cacheProvider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            var redisConn = configuration.GetValue<string>("Cache:Redis:ConnectionString") ?? "localhost:6379";
            services.AddSingleton<NexaOne.Driver.Redis.RedisDriver>();
            services.AddSingleton<NexaOne.Common.Caching.ICacheService>(sp =>
                new NexaOne.Driver.Redis.RedisCacheService(
                    sp.GetRequiredService<NexaOne.Driver.Redis.RedisDriver>(), redisConn));
        }
        else
        {
            services.AddSingleton<NexaOne.Common.Caching.ICacheService,
                NexaOne.Common.Caching.MemoryCacheService>();
        }

        // MDM
        services.AddScoped<IEquipmentRepository, EquipmentRepository>();
        services.AddScoped<IPlantRepository, PlantRepository>();
        services.AddScoped<IAreaRepository, AreaRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICodeRepository, CodeRepository>();
        services.AddScoped<EquipmentService>();
        services.AddScoped<MdmMasterService>();

        // EPT
        services.AddScoped<IEquipmentAlarmRepository, EquipmentAlarmRepository>();
        services.AddScoped<IEquipmentStateMatrixRepository, EquipmentStateMatrixRepository>();
        services.AddScoped<IEquipmentStateRepository, EquipmentStateRepository>();
        services.AddScoped<EquipmentAlarmService>();
        services.AddScoped<EquipmentStateService>();

        // FDC
        services.AddScoped<IFdcInterlockRuleRepository, FdcInterlockRuleRepository>();
        services.AddScoped<IFdcInterlockHistoryRepository, FdcInterlockHistoryRepository>();
        services.AddScoped<IFdcEquipmentEndpointRepository, FdcEquipmentEndpointRepository>();
        services.AddScoped<IFdcAlarmConfigRepository, FdcAlarmConfigRepository>();
        services.AddScoped<IFdcAlarmHistoryRepository, FdcAlarmHistoryRepository>();
        services.AddScoped<IFdcParameterRepository, FdcParameterRepository>();
        services.AddScoped<IFdcParameterGroupRepository, FdcParameterGroupRepository>();
        services.AddScoped<IFdcCollectDataRepository, FdcCollectDataRepository>();
        services.AddScoped<FdcInterlockService>();
        services.AddScoped<FdcAlarmService>();
        services.AddScoped<FdcDataService>();
        services.AddScoped<FdcParameterGroupService>();
        services.AddScoped<FdcCollectorService>();

        // RMS
        services.AddScoped<IRecipeRepository, RecipeRepository>();
        services.AddScoped<IRecipeParamRepository, RecipeParamRepository>();
        services.AddScoped<RecipeService>();

        // QMS
        services.AddScoped<IDefectRepository, DefectRepository>();
        services.AddScoped<IDefectClassRepository, DefectClassRepository>();
        services.AddScoped<IInspectionSpecRepository, InspectionSpecRepository>();
        services.AddScoped<IInspectionResultRepository, InspectionResultRepository>();
        services.AddScoped<ISpcParamRepository, SpcParamRepository>();
        services.AddScoped<QmsService>();

        // EMS
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IMaintenancePlanRepository, MaintenancePlanRepository>();
        services.AddScoped<ISparePartRepository, SparePartRepository>();
        services.AddScoped<CmmsService>();
        services.AddScoped<MaintenancePlanService>();

        // PPM
        services.AddScoped<IProductionPlanRepository, ProductionPlanRepository>();
        services.AddScoped<IProductionOrderRepository, ProductionOrderRepository>();
        services.AddScoped<PomService>();
        services.AddScoped<ProductionOrderService>();
        // Lot TrackIn/TrackOut (§19.4) — 교차 모듈 마스터 검증은 ITrackingMasterGateway 어댑터로 연결
        services.AddScoped<ILotRepository, LotRepository>();
        services.AddScoped<ILotHistoryRepository, LotHistoryRepository>();
        services.AddScoped<ILotMixingRelationRepository, LotMixingRelationRepository>();
        services.AddScoped<ITrackingMasterGateway, TrackingMasterGateway>();
        services.AddScoped<LotTrackingService>();

        // DLV
        services.AddScoped<IDeliveryOrderRepository, DeliveryOrderRepository>();
        services.AddScoped<IDeliveryItemRepository, DeliveryItemRepository>();
        services.AddScoped<IShipmentHistoryRepository, ShipmentHistoryRepository>();
        services.AddScoped<ShpService>();

        // SYS
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ILoginFailureHistoryRepository, LoginFailureHistoryRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IMultiLanguageResourceRepository, MultiLanguageResourceRepository>();
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<IConditionSettingRepository, ConditionSettingRepository>();
        // Phase 4 후속 — Low-Code 화면 정의 저장소(메타데이터)
        services.AddScoped<NexaOne.SYS.Application.Screens.IScreenDefinitionStore,
            NexaOne.SYS.Infrastructure.ScreenDefinitionStore>();
        services.AddScoped<UserService>();
        services.AddScoped<MenuService>();
        // 사용자 등록 신청/승인 (§19.3) — 신청 생명주기는 SYS_USER_REQUEST가 단독 소유
        services.AddScoped<IUserRequestRepository, UserRequestRepository>();
        services.AddScoped<UserRegistrationService>();
        // 조건 저장 한도 — 현행 App.config SaveConditionCount=10 대응 (설계 20.8)
        services.AddScoped(sp => new ConditionSettingService(
            sp.GetRequiredService<IConditionSettingRepository>(),
            configuration.GetValue("ConditionSetting:MaxSavedCount", ConditionSettingService.DefaultMaxSavedConditions)));

        // User menu personalization (§20.12) — 즐겨찾기/최근 메뉴
        services.AddScoped<IFavoriteMenuRepository, FavoriteMenuRepository>();
        services.AddScoped<IRecentMenuRepository, RecentMenuRepository>();
        services.AddScoped<UserMenuService>();

        // Deploy (§20.11) — 메타데이터는 DB, 바이너리는 API 서버 디스크(Deploy:StoragePath) 보관
        services.AddScoped<IDeployFileRepository, DeployFileRepository>();
        var deployRoot = configuration.GetValue<string>("Deploy:StoragePath");
        if (string.IsNullOrWhiteSpace(deployRoot))
            deployRoot = Path.Combine(AppContext.BaseDirectory, "App_Data", "Deploy");
        services.AddSingleton<IDeployFileStorage>(new FileSystemDeployFileStorage(deployRoot));
        services.AddScoped<DeployService>();

        // Auth (§20.10) — JwtBearer 검증(Program.cs)과 같은 Jwt:SecretKey를 사용한다
        services.AddSingleton<IJwtService, JwtService>();
        services.AddSingleton<IRefreshTokenStore, RefreshTokenStore>();

        // Email & password reset
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IMailTemplateService>(
            new MailTemplateService(Path.Combine(AppContext.BaseDirectory, "Config", "Mail")));
        services.AddScoped<PasswordResetService>();
        services.AddScoped<UserApprovalService>();

        return services;
    }
}
