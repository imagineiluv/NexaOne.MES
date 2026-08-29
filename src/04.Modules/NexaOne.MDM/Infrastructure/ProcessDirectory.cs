using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>MDM 공정 마스터 존재 여부를 제공하는 owner adapter입니다.</summary>
public sealed class ProcessDirectory : QueryRepository, IProcessDirectory
{
    public ProcessDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<bool> ProcessExistsAsync(
        string processId,
        CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM MDM_PROCESS WHERE PROCESS_ID = @processId",
            new { processId },
            ct) > 0;
}
