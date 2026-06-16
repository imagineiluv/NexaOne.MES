using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Sqlite;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// Spring.NET 인스턴스화용 <see cref="SqliteProvider"/> 정적 팩토리.
/// SqliteProvider는 전(全) 선택적 파라미터 ctor만 가져(C# 선택적 파라미터는 실제 무인자 ctor를 만들지 않음)
/// Spring.NET의 zero-arg 리플렉션 생성이 실패한다. 또한 sealed라 무인자 ctor를 가진 파생도 불가하다.
/// 따라서 server.xml에서 factory-method로 우회한다:
///   &lt;object id="dbProvider" type="NexaOne.Infrastructure.Persistence.SqliteProviderFactory, NexaOne.Infrastructure" factory-method="Create" /&gt;
/// MsSqlProvider는 진짜 무인자 ctor가 있어 이 우회가 필요 없다(직접 type 지정).
/// </summary>
public static class SqliteProviderFactory
{
    public static IDatabaseProvider Create() => new SqliteProvider();
}
