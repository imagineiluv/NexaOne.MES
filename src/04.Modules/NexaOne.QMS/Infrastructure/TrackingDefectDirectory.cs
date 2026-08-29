using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.QMS.Infrastructure;

/// <summary>활성·미삭제 불량 분류를 제공하는 QMS owner adapter입니다.</summary>
public sealed class TrackingDefectDirectory : QueryRepository, ITrackingDefectDirectory
{
    public TrackingDefectDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<bool> IsValidAsync(string defectCode, CancellationToken ct = default)
        => await CountAsync(
            @"SELECT COUNT(*) FROM QMS_DEFECT_CLASS
              WHERE DEFECT_CLASS_ID = @defectCode
                AND IS_ACTIVE = 1
                AND IS_DELETED = 0",
            new { defectCode },
            ct) > 0;
}
