using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.MsSql;
using NexaOne.API.Services;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Application.Messaging;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Infrastructure;
using NexaOne.EPT.Application.Ept;
using NexaOne.EPT.Infrastructure;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Infrastructure;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Infrastructure;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Infrastructure;
using NexaOne.PPM.Application.Ppm;
using NexaOne.PPM.Infrastructure;
using NexaOne.DLV.Application.Dlv;
using NexaOne.DLV.Infrastructure;
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

        var provider = new MsSqlProvider();
        var dataSource = new EesDataSource
        {
            Provider = provider,
            ConnectionString = connStr
        };

        services.AddSingleton<IDatabaseProvider>(provider);
        services.AddSingleton<INexaOneEESDbCapability>(provider);
        services.AddSingleton(dataSource);

        services.AddScoped<SqlTxnContext>();
        services.AddScoped<QueryRepository>();
        services.AddScoped(sp =>
            new ServiceObjectProcessor(
                sp.GetRequiredService<EesDataSource>(),
                sp.GetService<IHttpContextAccessor>()?.HttpContext?.User?.Identity?.Name ?? "SYSTEM"));

        services.AddHttpContextAccessor();

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
        services.AddScoped<IFdcParameterRepository, FdcParameterRepository>();
        services.AddScoped<IFdcCollectDataRepository, FdcCollectDataRepository>();
        services.AddScoped<FdcInterlockService>();
        services.AddScoped<FdcDataService>();

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
        services.AddScoped<EmsService>();
        services.AddScoped<MaintenancePlanService>();

        // PPM
        services.AddScoped<IProductionPlanRepository, ProductionPlanRepository>();
        services.AddScoped<IProductionOrderRepository, ProductionOrderRepository>();
        services.AddScoped<PpmService>();
        services.AddScoped<ProductionOrderService>();

        // DLV
        services.AddScoped<IDeliveryOrderRepository, DeliveryOrderRepository>();
        services.AddScoped<IDeliveryItemRepository, DeliveryItemRepository>();
        services.AddScoped<IShipmentHistoryRepository, ShipmentHistoryRepository>();
        services.AddScoped<DlvService>();

        // SYS
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ILoginFailureHistoryRepository, LoginFailureHistoryRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IMultiLanguageResourceRepository, MultiLanguageResourceRepository>();
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<IConditionSettingRepository, ConditionSettingRepository>();
        services.AddScoped<UserService>();
        services.AddScoped<MenuService>();
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

        return services;
    }
}
