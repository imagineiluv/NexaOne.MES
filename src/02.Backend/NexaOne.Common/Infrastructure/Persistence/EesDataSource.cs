using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

public sealed class EesDataSource
{
    static EesDataSource()
    {
        // 모든 리포지토리가 SELECT * (SNAKE_CASE 컬럼) → PascalCase Row 프로퍼티 매핑에
        // 의존하므로 Dapper 언더스코어 매핑을 전역으로 활성화한다.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public IDatabaseProvider Provider { get; set; } = null!;
    public string ConnectionString { get; set; } = string.Empty;

    internal DatabaseEndpoint CreateEndpoint() =>
        new("NexaOneEES", Provider.Kind, ConnectionString);
}
