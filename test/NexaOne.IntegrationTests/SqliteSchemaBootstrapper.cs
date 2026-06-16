using NexaOne.Infrastructure.Persistence;

namespace NexaOne.IntegrationTests;

/// <summary>
/// 통합 테스트용 SQLite 스키마 부트스트랩 — 실제 변환/적용 로직은 운영 코드(NexaOne.Infrastructure의
/// <see cref="SqliteSchemaInitializer"/>)와 단일 구현을 공유한다(NexaOne.Server SQLite 모드와 동일 경로).
/// 테스트는 매번 새 임시 DB를 만들므로 빈 DB 가정의 Apply를 직접 호출한다.
/// </summary>
internal static class SqliteSchemaBootstrapper
{
    public static void Apply(string connectionString) =>
        SqliteSchemaInitializer.Apply(connectionString);
}
