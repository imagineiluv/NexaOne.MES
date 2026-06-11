using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Application.Equipments;

public interface ICodeRepository
{
    Task<CodeClass?> GetClassByIdAsync(string codeClassId, CancellationToken ct = default);
    Task<IReadOnlyList<CodeClass>> GetAllClassesAsync(CancellationToken ct = default);
    Task AddClassAsync(CodeClass codeClass, CancellationToken ct = default);
    Task<IReadOnlyList<Code>> GetByClassAsync(string codeClassId, CancellationToken ct = default);
    Task AddCodeAsync(Code code, CancellationToken ct = default);
}
