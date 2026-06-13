using System.Collections.Concurrent;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>인메모리 명명 쿼리 카탈로그(ADR-001). 시작 시 쿼리를 등록하고 게이트웨이가 이름으로 해석한다.</summary>
public sealed class InMemoryQueryCatalog : IQueryCatalog
{
    /// <summary>프로세스 공용 카탈로그 — QueryRepository가 명명 쿼리 해석에 사용한다.</summary>
    public static InMemoryQueryCatalog Shared { get; } = new();

    private readonly ConcurrentDictionary<string, string> _queries = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, string sql)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Query name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL is required.", nameof(sql));
        _queries[name] = sql;
    }

    public bool TryResolve(string name, out string sql)
    {
        if (!string.IsNullOrEmpty(name) && _queries.TryGetValue(name, out var found))
        {
            sql = found;
            return true;
        }
        sql = string.Empty;
        return false;
    }

    public string Resolve(string name)
        => TryResolve(name, out var sql)
            ? sql
            : throw new KeyNotFoundException($"Named query '{name}' is not registered in the catalog.");
}
