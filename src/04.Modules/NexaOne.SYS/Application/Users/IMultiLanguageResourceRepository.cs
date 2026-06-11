using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

public interface IMultiLanguageResourceRepository
{
    Task<MultiLanguageResource?> GetByIdAsync(string resourceKey, CancellationToken ct = default);
    Task<IReadOnlyList<MultiLanguageResource>> GetByMenuIdAsync(string menuId, CancellationToken ct = default);
    Task<IReadOnlyList<MultiLanguageResource>> GetByLanguageAsync(LanguageType language, CancellationToken ct = default);
    Task<bool> ExistsAsync(string resourceKey, CancellationToken ct = default);
    Task AddAsync(MultiLanguageResource resource, CancellationToken ct = default);
    Task UpdateAsync(MultiLanguageResource resource, CancellationToken ct = default);
}
