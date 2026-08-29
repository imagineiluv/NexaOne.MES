using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;

namespace NexaOne.QMS.Infrastructure;

/// <summary>QMS persistence adapter for the production quality-gate evidence projection.</summary>
public sealed class ProductionQualityGateEvidenceRepository
    : QueryRepository, IProductionQualityGateEvidenceRepository
{
    public ProductionQualityGateEvidenceRepository(EesDataSource dataSource) : base(dataSource) { }

    public Task<IReadOnlyList<ProductionQualityGateEvidence>> GetLatestAsync(
        string lotId,
        string processId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH ProcessResults AS (
                SELECT R.SPEC_ID,
                       R.RESULT_ID,
                       R.INSPECTED_AT,
                       R.IS_PASS,
                       I.INSPECTION_ID,
                       I.IS_CONFIRMED,
                       I.RESULT AS HEADER_RESULT,
                       CASE WHEN I.IDEMPOTENCY_KEY IS NOT NULL THEN 1 ELSE 0 END AS IS_V2,
                       CASE WHEN EXISTS (
                           SELECT 1
                           FROM QMS_INSPECTION_EVENT E
                           WHERE E.INSPECTION_ID = I.INSPECTION_ID
                             AND E.EVENT_TYPE = 'Cancelled'
                       ) THEN 1 ELSE 0 END AS IS_CANCELLED,
                       CASE WHEN EXISTS (
                           SELECT 1
                           FROM QMS_INSPECTION_EVENT E
                           INNER JOIN QMS_INSPECTION SUCCESSOR
                             ON SUCCESSOR.INSPECTION_ID = E.RELATED_INSPECTION_ID
                            AND SUCCESSOR.PARENT_INSPECTION_ID = I.INSPECTION_ID
                            AND SUCCESSOR.ROOT_INSPECTION_ID = I.ROOT_INSPECTION_ID
                            AND SUCCESSOR.LOT_ID = I.LOT_ID
                            AND SUCCESSOR.INSPECTION_TYPE = I.INSPECTION_TYPE
                           WHERE E.INSPECTION_ID = I.INSPECTION_ID
                             AND E.EVENT_TYPE IN ('Corrected', 'Reinspected')
                       ) THEN 1 ELSE 0 END AS IS_SUPERSEDED
                FROM QMS_INSPECTION_RESULT R
                INNER JOIN QMS_INSPECTION I
                  ON I.INSPECTION_ID = R.INSPECTION_ID
                 AND I.LOT_ID = R.LOT_ID
                 AND (I.SPEC_ID = R.SPEC_ID OR
                      (I.IDEMPOTENCY_KEY IS NOT NULL AND I.SPEC_ID IS NULL))
                WHERE R.LOT_ID = @lotId
                  AND I.INSPECTION_TYPE = 'Process'
            ),
            RankedProcessResults AS (
                SELECT P.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY P.SPEC_ID
                           ORDER BY P.INSPECTED_AT DESC, P.INSPECTION_ID DESC, P.RESULT_ID DESC
                       ) AS RESULT_RANK
                FROM ProcessResults P
            )
            SELECT S.SPEC_ID,
                   R.RESULT_ID,
                   R.INSPECTED_AT,
                   R.IS_PASS,
                   R.INSPECTION_ID,
                   R.IS_CONFIRMED,
                   R.HEADER_RESULT,
                   COALESCE(R.IS_V2, 0) AS IS_V2,
                   COALESCE(R.IS_CANCELLED, 0) AS IS_CANCELLED,
                   COALESCE(R.IS_SUPERSEDED, 0) AS IS_SUPERSEDED
            FROM QMS_INSPECTION_SPEC S
            LEFT JOIN RankedProcessResults R
              ON R.SPEC_ID = S.SPEC_ID
             AND R.RESULT_RANK = 1
            WHERE S.PROCESS_ID = @processId
              AND S.IS_ACTIVE = 1
            ORDER BY S.SPEC_ID
            """;
        return QueryAsync<ProductionQualityGateEvidence>(
            sql,
            new { lotId, processId },
            cancellationToken);
    }
}
