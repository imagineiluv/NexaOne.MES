using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application;
using NexaOne.Infrastructure.Persistence;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.MsSql;

namespace NexaOne.Server.Gateway;

/// <summary>게이트웨이(하이브리드) 데이터 경로 DI — DB 공급자 + EesDataSource + 명명 쿼리 게이트웨이
/// (IRuleDispatcher·IQueryRegistry, AddNexaOneEES). plugin↔DI 브리지 없이 Default ALC만 사용한다.
/// DB 선택은 ASP.NET config(Database:Provider) 기준 — server.xml(Spring)과 별개로 게이트웨이 전용으로 등록한다.</summary>
public static class GatewayServiceExtensions
{
    public static IServiceCollection AddNexaOneGateway(this IServiceCollection services, IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString("NexaOne")
            ?? throw new InvalidOperationException("ConnectionStrings:NexaOne is required for the gateway data path");

        var dbProvider = configuration.GetValue<string>("Database:Provider") ?? "MsSql";
        IDatabaseProvider provider;
        INexaOneEESDbCapability capability;
        if (string.Equals(dbProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            provider = new NexaDB.Data.Sqlite.SqliteProvider();
            capability = new SqliteEesDbCapability();
        }
        else
        {
            var mssql = new MsSqlProvider();
            provider = mssql;
            capability = mssql;
        }

        services.AddSingleton(provider);
        services.AddSingleton(capability);
        services.AddSingleton(new EesDataSource { Provider = provider, ConnectionString = connStr });

        services.AddNexaOneEES(configuration);
        return services;
    }
}
