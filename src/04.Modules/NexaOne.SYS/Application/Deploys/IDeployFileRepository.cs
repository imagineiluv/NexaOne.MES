using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Deploys;

public interface IDeployFileRepository
{
    Task<IReadOnlyList<DeployFile>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeployFile>> GetActiveAsync(CancellationToken ct = default);
    Task<DeployFile?> GetByIdAsync(string fileId, CancellationToken ct = default);
    Task<DeployFile?> GetByVersionAsync(string version, CancellationToken ct = default);
    Task InsertAsync(DeployFile file, CancellationToken ct = default);
    Task UpdateAsync(DeployFile file, CancellationToken ct = default);
}
