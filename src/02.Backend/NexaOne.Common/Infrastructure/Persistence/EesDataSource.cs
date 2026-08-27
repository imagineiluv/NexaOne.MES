using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;
using NexaDB.Diagnostics;

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

    /// <summary>
    /// 이 데이터소스로 만들어지는 읽기 게이트웨이의 공통 운영 옵션. 기존 Spring.NET 및 Microsoft DI 구성은
    /// 속성을 생략하면 공급자 기본 제한 시간을 그대로 사용한다.
    /// </summary>
    public DapperQueryGatewayOptions QueryGatewayOptions { get; set; } = new();

    /// <summary>
    /// 선택적인 NexaDB 진단 sink. 설정하면 이 데이터소스를 공유하는 <see cref="QueryRepository"/> 읽기 경로가
    /// 안전한 쿼리 진단을 자동 발행한다.
    /// </summary>
    public IDiagnosticEventSink? QueryDiagnosticSink { get; set; }

    internal DatabaseEndpoint CreateEndpoint() =>
        new("NexaOneEES", Provider.Kind, ConnectionString);
}
