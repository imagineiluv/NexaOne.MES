using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NexaOne.Application.Messaging;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Application;

public static class NexaOneEesServiceExtensions
{
    public static IServiceCollection AddNexaOneEES(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString("NexaOne") ?? string.Empty;
        services.AddSingleton<IRuleDispatcher>(sp =>
            new NexaFrameworkRuleDispatcher(
                sp.GetRequiredService<IDatabaseProvider>(),
                connStr));
        return services;
    }
}

internal sealed class NexaFrameworkRuleDispatcher : IRuleDispatcher
{
    private readonly IDatabaseProvider _provider;
    private readonly string _connectionString;

    public NexaFrameworkRuleDispatcher(IDatabaseProvider provider, string connectionString)
    {
        _provider = provider;
        _connectionString = connectionString;
    }

    public async Task<object?> DispatchAsync(
        string ruleName,
        IDictionary<string, object> body,
        CancellationToken ct = default)
    {
        var server = NexusFramework.ApplicationServer.GetInstance();
        try
        {
            var bean = server.GetBean("NexaOne", ruleName);
            var beanType = bean.GetType();

            // Try async: ExecuteAsync(IDictionary<string, object>, CancellationToken)
            var asyncMethod = beanType.GetMethod("ExecuteAsync",
                new[] { typeof(IDictionary<string, object>), typeof(CancellationToken) });
            if (asyncMethod != null)
            {
                var task = asyncMethod.Invoke(bean, new object[] { body, ct });
                if (task is Task<object?> typed) return await typed;
                if (task is Task t) { await t; return null; }
            }

            // Fallback: Execute(IDictionary<string, object>)
            var syncMethod = beanType.GetMethod("Execute",
                new[] { typeof(IDictionary<string, object>) });
            if (syncMethod != null)
                return syncMethod.Invoke(bean, new object[] { body });

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        string sql,
        IDictionary<string, object> parameters,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var param = parameters.Count > 0 ? new DynamicParameters(parameters) : null;
        var rows = await conn.QueryAsync(sql, param).ConfigureAwait(false);
        return rows
            .Select(row => ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    public async Task<object?> ProcedureAsync(
        string procedureName,
        IDictionary<string, object> parameters,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var param = new DynamicParameters(parameters);
        return await conn.ExecuteScalarAsync(
            procedureName, param,
            commandType: System.Data.CommandType.StoredProcedure).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, object>> ProcedureToDataSetAsync(
        string procedureName,
        IDictionary<string, object> parameters,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var param = new DynamicParameters(parameters);
        using var multi = await conn.QueryMultipleAsync(
            procedureName, param,
            commandType: System.Data.CommandType.StoredProcedure).ConfigureAwait(false);

        var result = new Dictionary<string, object>();
        var index = 0;
        while (!multi.IsConsumed)
        {
            var rows = (await multi.ReadAsync().ConfigureAwait(false)).ToList();
            result[$"Table{index++}"] = rows;
        }
        return result;
    }
}
