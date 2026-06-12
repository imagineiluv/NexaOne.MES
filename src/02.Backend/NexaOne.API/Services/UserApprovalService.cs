using System.Security.Cryptography;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.API.Services;

/// <summary>
/// §19.3 — 등록 신청/승인/반려 오케스트레이션. 생명주기 전이는 UserRegistrationService(SYS)가,
/// 임시 비밀번호 생성·메일 발송·감사 로그는 평문/메일 인프라가 있는 이 API 계층이 담당한다
/// (PasswordResetService와 동일한 책임 분리). 메일 실패는 흐름을 막지 않고 로그만 남긴다.
/// 설계 표준의 감사 로그(UserRequestCreated/Approved/Rejected)는 Serilog 구조화 로그로 적응.
/// </summary>
public sealed class UserApprovalService
{
    private const string TemplateName = "InitPassword";

    private readonly UserRegistrationService _registration;
    private readonly IEmailSender _emailSender;
    private readonly IMailTemplateService _mailTemplate;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserApprovalService> _logger;

    public UserApprovalService(
        UserRegistrationService registration,
        IEmailSender emailSender,
        IMailTemplateService mailTemplate,
        IConfiguration configuration,
        ILogger<UserApprovalService> logger)
    {
        _registration = registration;
        _emailSender = emailSender;
        _mailTemplate = mailTemplate;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>§19.3.3 — 등록 신청. 성공 시 관리자 알림(설정 시 메일, 항상 구조화 로그)을 남긴다.</summary>
    public async Task<Result<UserRequest>> RequestAsync(
        UserRegistrationCommand command, CancellationToken ct = default)
    {
        var result = await _registration.RequestAsync(command, DateTime.UtcNow, ct);
        if (result.IsFailure)
            return result;

        var request = result.Value;
        _logger.LogInformation(
            "UserRequestCreated: {RequestId} user={UserId} plant={PlantId} version={Version} ip={Ip}",
            request.Id, request.UserId, request.PlantId, request.RequestVersion, request.TermsAcceptedIp);

        // 설계 표준의 관리자 알림 메일 — 관리자 수신 주소 설정이 있을 때만 발송 (없으면 로그로 충분)
        var adminEmail = _configuration["Registration:AdminEmail"];
        if (!string.IsNullOrWhiteSpace(adminEmail))
        {
            try
            {
                await _emailSender.SendAsync(
                    to: adminEmail,
                    subject: "[NexaOne MES] 신규 사용자 등록 신청",
                    body: $"사용자 등록 신청이 접수되었습니다.\n\n아이디: {request.UserId}\n이름: {request.UserName}\n" +
                          $"부서: {request.Department}\n플랜트: {request.PlantId}\n\n승인 화면에서 처리해 주세요.",
                    isHtml: false,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserRequest: admin notification mail failed for {UserId}", request.UserId);
            }
        }

        return result;
    }

    /// <summary>§19.3.5 — 승인. 임시 비밀번호를 생성·해시 저장(PasswordState=Create)하고
    /// InitPassword 템플릿(${PASSWORD} 치환)으로 안내 메일을 발송한다. 원문은 메일로만 전달된다.</summary>
    public async Task<Result<UserRequest>> ApproveAsync(
        string requestId, string approvedBy, string roleId, CancellationToken ct = default)
    {
        var tempPassword = GenerateTempPassword();
        var hash = PasswordHasher.Hash(tempPassword);

        var result = await _registration.ApproveAsync(
            requestId, approvedBy, roleId, hash, DateTime.UtcNow, ct);
        if (result.IsFailure)
            return Result.Failure<UserRequest>(result.Error);

        var (request, user) = result.Value;
        _logger.LogInformation(
            "UserRequestApproved: {RequestId} user={UserId} role={RoleId} by={ApprovedBy}",
            request.Id, user.Id, user.RoleId, approvedBy);

        // §19.3.5 — 현행 메일 템플릿 구조(Config/Mail/InitPassword({culture}).xml) 재사용
        var body = _mailTemplate.Render(TemplateName, CultureOf(user.Language), new Dictionary<string, string>
        {
            ["PASSWORD"] = tempPassword,
            ["USERNAME"] = user.UserName
        });
        var isHtml = body is not null;
        body ??= $"안녕하세요 {user.UserName}님,\n\n등록 신청이 승인되었습니다.\n임시 비밀번호: {tempPassword}\n\n" +
                 "로그인 후 반드시 비밀번호를 변경해 주세요.\n\n감사합니다.";

        try
        {
            await _emailSender.SendAsync(
                to: user.Email,
                subject: "[NexaOne MES] 가입 승인 및 임시 비밀번호 안내",
                body: body,
                isHtml: isHtml,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserRequestApproved: mail send failed for {UserId}", user.Id);
        }

        return Result.Success(request);
    }

    /// <summary>§19.3.6 — 반려. 사유를 기록하고 신청자에게 안내 메일을 발송한다(실패 시 로그만).</summary>
    public async Task<Result<UserRequest>> RejectAsync(
        string requestId, string rejectedBy, string reason, CancellationToken ct = default)
    {
        var result = await _registration.RejectAsync(requestId, rejectedBy, reason, DateTime.UtcNow, ct);
        if (result.IsFailure)
            return result;

        var request = result.Value;
        _logger.LogInformation(
            "UserRequestRejected: {RequestId} user={UserId} by={RejectedBy} reason={Reason}",
            request.Id, request.UserId, rejectedBy, request.RejectReason);

        try
        {
            await _emailSender.SendAsync(
                to: request.Email,
                subject: "[NexaOne MES] 가입 신청 반려 안내",
                body: $"안녕하세요 {request.UserName}님,\n\n등록 신청이 반려되었습니다.\n" +
                      $"사유: {request.RejectReason}\n\n수정 후 다시 신청하실 수 있습니다.\n\n감사합니다.",
                isHtml: false,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UserRequestRejected: mail send failed for {UserId}", request.UserId);
        }

        return result;
    }

    private static string CultureOf(LanguageType language) => language switch
    {
        LanguageType.EnUs => "en-US",
        LanguageType.ZhCn => "zh-CN",
        LanguageType.ViVn => "vi-VN",
        LanguageType.LoLo => "lo-LA",
        _ => "ko-KR"
    };

    private static string GenerateTempPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";
        var bytes = RandomNumberGenerator.GetBytes(12);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}
