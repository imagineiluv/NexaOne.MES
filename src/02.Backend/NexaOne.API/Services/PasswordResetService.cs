using System.Security.Cryptography;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;

namespace NexaOne.API.Services;

public sealed class PasswordResetService
{
    private const string TemplateName = "InitPassword";

    private readonly UserService _userService;
    private readonly IEmailSender _emailSender;
    private readonly IMailTemplateService _mailTemplate;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        UserService userService,
        IEmailSender emailSender,
        IMailTemplateService mailTemplate,
        ILogger<PasswordResetService> logger)
    {
        _userService = userService;
        _emailSender = emailSender;
        _mailTemplate = mailTemplate;
        _logger = logger;
    }

    // §20.10 — 아이디/이메일 불일치 시에도 항상 동일하게 반환한다 (계정 열거 방지).
    // 상세 사유는 서버 로그에만 남긴다.
    public async Task ForgotPasswordAsync(string userId, string email, CancellationToken ct = default)
    {
        var userResult = await _userService.GetUserAsync(userId, ct);
        if (userResult.IsFailure)
        {
            _logger.LogWarning("ForgotPassword: unknown user {UserId}", userId);
            return;
        }

        var user = userResult.Value;
        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("ForgotPassword: email mismatch for {UserId}", userId);
            return;
        }

        var tempPassword = GenerateTempPassword();
        var hash = PasswordHasher.Hash(tempPassword);

        // 임시 비밀번호는 즉시 해시 저장 + 상태 Forgot 표시 (§20.10) — 원문은 메일로만 전달
        var changeResult = await _userService.ForceSetPasswordAsync(userId, hash, ct);
        if (changeResult.IsFailure)
        {
            _logger.LogWarning("ForgotPassword: failed to reset password for {UserId}", userId);
            return;
        }

        // §20.10 — 현행 메일 템플릿 구조(Config/Mail/InitPassword({culture}).xml, ${PASSWORD} 치환)를 따른다
        var body = _mailTemplate.Render(TemplateName, CultureOf(user.Language), new Dictionary<string, string>
        {
            ["PASSWORD"] = tempPassword,
            ["USERNAME"] = user.UserName
        });
        var isHtml = body is not null;
        body ??= $"안녕하세요 {user.UserName}님,\n\n임시 비밀번호: {tempPassword}\n\n로그인 후 반드시 비밀번호를 변경해 주세요.\n\n감사합니다.";

        try
        {
            await _emailSender.SendAsync(
                to: user.Email,
                subject: "[NexaOne MES] 비밀번호 초기화 알림",
                body: body,
                isHtml: isHtml,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ForgotPassword: email send failed for {UserId}", userId);
        }
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
