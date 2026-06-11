using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Infrastructure;

public sealed class CodeRepository : QueryRepository, ICodeRepository
{
    private readonly ServiceObjectProcessor _processor;

    public CodeRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<CodeClass?> GetClassByIdAsync(string codeClassId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_CODE_CLASS WITH(NOLOCK) WHERE CODE_CLASS_ID = @codeClassId";
        var row = await QueryFirstOrDefaultAsync<CodeClassRow>(sql, new { codeClassId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<CodeClass>> GetAllClassesAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM MDM_CODE_CLASS WITH(NOLOCK) ORDER BY CODE_CLASS_NAME";
        var rows = await QueryAsync<CodeClassRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<CodeClass>().ToList();
    }

    public async Task AddClassAsync(CodeClass codeClass, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_CODE_CLASS
            (CODE_CLASS_ID, CODE_CLASS_NAME, DESCRIPTION,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@CodeClassId, @CodeClassName, @Description,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, CodeClassRow.FromDomain(codeClass), ct);
    }

    public async Task<IReadOnlyList<Code>> GetByClassAsync(string codeClassId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM MDM_CODE WITH(NOLOCK)
            WHERE CODE_CLASS_ID = @codeClassId AND VALID_STATE = 'Valid'
            ORDER BY SORT_ORDER, CODE_NAME";
        var rows = await QueryAsync<CodeRow>(sql, new { codeClassId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<Code>().ToList();
    }

    public async Task AddCodeAsync(Code code, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO MDM_CODE
            (CODE_ID, CODE_CLASS_ID, CODE_NAME, SORT_ORDER, VALID_STATE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@CodeId, @CodeClassId, @CodeName, @SortOrder, @ValidState,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, CodeRow.FromDomain(code), ct);
    }

    private sealed class CodeClassRow
    {
        public string CodeClassId   { get; set; } = "";
        public string CodeClassName { get; set; } = "";
        public string Description   { get; set; } = "";

        public CodeClass? ToDomain() => CodeClass.Create(CodeClassId, CodeClassName).Value;

        public static CodeClassRow FromDomain(CodeClass c) => new()
        {
            CodeClassId = c.Id, CodeClassName = c.CodeClassName, Description = c.Description
        };
    }

    private sealed class CodeRow
    {
        public string CodeId       { get; set; } = "";
        public string CodeClassId  { get; set; } = "";
        public string CodeName     { get; set; } = "";
        public int    SortOrder    { get; set; }
        public string ValidState   { get; set; } = "Valid";

        public Code? ToDomain() => Code.Create(CodeId, CodeClassId, CodeName, SortOrder).Value;

        public static CodeRow FromDomain(Code c) => new()
        {
            CodeId = c.Id, CodeClassId = c.CodeClassId,
            CodeName = c.CodeName, SortOrder = c.SortOrder, ValidState = c.ValidState
        };
    }
}
