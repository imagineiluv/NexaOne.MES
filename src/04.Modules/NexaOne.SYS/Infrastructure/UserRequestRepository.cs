using System.Text;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class UserRequestRepository : QueryRepository, IUserRequestRepository
{
    private readonly ServiceObjectProcessor _processor;

    public UserRequestRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<UserRequest?> GetByIdAsync(string requestId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_USER_REQUEST WHERE REQUEST_ID = @requestId";
        var row = await QueryFirstOrDefaultAsync<UserRequestRow>(sql, new { requestId }, ct);
        return row?.ToDomain();
    }

    public async Task<UserRequest?> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_USER_REQUEST WHERE USER_ID = @userId";
        var row = await QueryFirstOrDefaultAsync<UserRequestRow>(sql, new { userId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<UserRequest>> SearchAsync(
        string? plantId, string? status, string? userId, string? userName, string? email,
        DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        // LIKE 와일드카드는 SQL이 아니라 파라미터 값에 넣는다(LIKE @p). MSSQL '+' 문자열 결합은 SQLite에서
        // 산술('%' + @p → 0)로 평가돼 필터가 항상 빈 결과를 반환하므로 — 방언 무관한 바운드 파라미터로 통일한다.
        var sql = new StringBuilder("SELECT * FROM SYS_USER_REQUEST WHERE 1=1");
        if (plantId is not null)  sql.Append(" AND PLANT_ID = @plantId");
        if (status is not null)   sql.Append(" AND STATUS = @status");
        if (userId is not null)   sql.Append(" AND USER_ID LIKE @userId");
        if (userName is not null) sql.Append(" AND USER_NAME LIKE @userName");
        if (email is not null)    sql.Append(" AND EMAIL LIKE @email");
        if (from.HasValue)        sql.Append(" AND REQUESTED_AT >= @from");
        if (to.HasValue)          sql.Append(" AND REQUESTED_AT <= @to");
        sql.Append(" ORDER BY REQUESTED_AT DESC");

        var rows = await QueryAsync<UserRequestRow>(
            sql.ToString(),
            new
            {
                plantId, status,
                userId   = userId   is null ? null : $"%{userId}%",
                userName = userName is null ? null : $"%{userName}%",
                email    = email    is null ? null : $"%{email}%",
                from, to
            }, ct);
        return rows.Select(r => r.ToDomain()).OfType<UserRequest>().ToList();
    }

    public async Task AddAsync(UserRequest request, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SYS_USER_REQUEST
            (REQUEST_ID, USER_ID, USER_NAME, EMAIL, DEPARTMENT, POSITION, DUTY,
             PLANT_ID, LANGUAGE, CELL_PHONE, ADDRESS, DESCRIPTION, NICKNAME,
             STATUS, REQUEST_VERSION, REQUESTED_AT, TERMS_ACCEPTED_AT, TERMS_ACCEPTED_IP,
             APPROVED_BY, APPROVED_AT, REJECT_REASON, REJECTED_BY, REJECTED_AT,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@RequestId, @UserId, @UserName, @Email, @Department, @Position, @Duty,
             @PlantId, @Language, @CellPhone, @Address, @Description, @Nickname,
             @Status, @RequestVersion, @RequestedAt, @TermsAcceptedAt, @TermsAcceptedIp,
             @ApprovedBy, @ApprovedAt, @RejectReason, @RejectedBy, @RejectedAt,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, UserRequestRow.FromDomain(request), ct);
    }

    public async Task UpdateAsync(UserRequest request, CancellationToken ct = default)
    {
        // 승인/반려/재신청이 같은 행을 전환한다 — 상태 포함 전체 컬럼 갱신 (§19.3.7)
        const string sql = @"UPDATE SYS_USER_REQUEST SET
            USER_NAME = @UserName, EMAIL = @Email, DEPARTMENT = @Department,
            POSITION = @Position, DUTY = @Duty, PLANT_ID = @PlantId, LANGUAGE = @Language,
            CELL_PHONE = @CellPhone, ADDRESS = @Address, DESCRIPTION = @Description,
            NICKNAME = @Nickname, STATUS = @Status, REQUEST_VERSION = @RequestVersion,
            REQUESTED_AT = @RequestedAt,
            TERMS_ACCEPTED_AT = @TermsAcceptedAt, TERMS_ACCEPTED_IP = @TermsAcceptedIp,
            APPROVED_BY = @ApprovedBy, APPROVED_AT = @ApprovedAt,
            REJECT_REASON = @RejectReason, REJECTED_BY = @RejectedBy, REJECTED_AT = @RejectedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE REQUEST_ID = @RequestId";
        await _processor.UpdateAsync(sql, UserRequestRow.FromDomain(request), ct);
    }

    private sealed class UserRequestRow
    {
        public string RequestId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Department { get; set; } = "";
        public string Position { get; set; } = "";
        public string? Duty { get; set; }
        public string PlantId { get; set; } = "";
        public string Language { get; set; } = "KoKr";
        public string? CellPhone { get; set; }
        public string? Address { get; set; }
        public string? Description { get; set; }
        public string? Nickname { get; set; }
        public string Status { get; set; } = "Request";
        public int RequestVersion { get; set; } = 1;
        public DateTime RequestedAt { get; set; }
        public DateTime TermsAcceptedAt { get; set; }
        public string TermsAcceptedIp { get; set; } = "";
        public string? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public string? RejectReason { get; set; }
        public string? RejectedBy { get; set; }
        public DateTime? RejectedAt { get; set; }

        public UserRequest ToDomain()
        {
            if (!Enum.TryParse<LanguageType>(Language, out var lang) || !Enum.IsDefined(lang)) lang = LanguageType.KoKr;
            if (!Enum.TryParse<UserRequestStatus>(Status, out var status)) status = UserRequestStatus.Request;

            return UserRequest.Restore(
                RequestId, UserId, UserName, Email, Department, Position, Duty,
                PlantId, lang, CellPhone, Address, Description, Nickname,
                status, RequestVersion, RequestedAt, TermsAcceptedAt, TermsAcceptedIp,
                ApprovedBy, ApprovedAt, RejectReason, RejectedBy, RejectedAt);
        }

        public static UserRequestRow FromDomain(UserRequest r) => new()
        {
            RequestId = r.Id,
            UserId = r.UserId,
            UserName = r.UserName,
            Email = r.Email,
            Department = r.Department,
            Position = r.Position,
            Duty = r.Duty,
            PlantId = r.PlantId,
            Language = r.Language.ToString(),
            CellPhone = r.CellPhoneNumber,
            Address = r.Address,
            Description = r.Description,
            Nickname = r.Nickname,
            Status = r.Status.ToString(),
            RequestVersion = r.RequestVersion,
            RequestedAt = r.RequestedAt,
            TermsAcceptedAt = r.TermsAcceptedAt,
            TermsAcceptedIp = r.TermsAcceptedIp,
            ApprovedBy = r.ApprovedBy,
            ApprovedAt = r.ApprovedAt,
            RejectReason = r.RejectReason,
            RejectedBy = r.RejectedBy,
            RejectedAt = r.RejectedAt
        };
    }
}
