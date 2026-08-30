using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace NexaOne.Server.Security;

/// <summary>설비 설치 client가 공유하는 secret header와 구성 경로를 한 곳에 고정합니다.</summary>
public static class EquipmentClientAuthentication
{
    public const string ClientSecretHeader = "X-Nexa-Run-Client-Secret";
    internal const string ClientsConfigurationPath = "RunAdmission:Clients";
    internal const string RequireHttpsConfigurationKey = "RunAdmission:RequireHttps";
}

/// <summary>Defines the HTTP boundary shared by secret-authenticated equipment clients.</summary>
internal static class EquipmentClientEndpointPolicy
{
    internal const string RunAdmissionPath = "/api/v1/run-admission";
    internal const string WorkScopeProjectionPath = "/api/v1/pom/work-scope-projections";

    internal static bool IsEquipmentClientPath(PathString path) =>
        path.StartsWithSegments(RunAdmissionPath)
        || path.StartsWithSegments(WorkScopeProjectionPath);
}

public sealed record EquipmentClientIdentity(string ClientId, string EquipmentId);

public enum EquipmentClientAuthenticationFailureKind
{
    InvalidRequest,
    Unauthorized,
    Forbidden,
    HttpsRequired,
    ConfigurationUnavailable,
}

public sealed record EquipmentClientAuthenticationRejection(
    EquipmentClientAuthenticationFailureKind Kind,
    string? Title = null,
    string? Detail = null)
{
    public IActionResult ToActionResult() => Kind switch
    {
        EquipmentClientAuthenticationFailureKind.Unauthorized => new UnauthorizedResult(),
        EquipmentClientAuthenticationFailureKind.Forbidden =>
            new StatusCodeResult(StatusCodes.Status403Forbidden),
        EquipmentClientAuthenticationFailureKind.InvalidRequest => new BadRequestObjectResult(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = Title ?? "The equipment client request is invalid.",
                Detail = Detail,
            }),
        EquipmentClientAuthenticationFailureKind.HttpsRequired => Problem(
            StatusCodes.Status426UpgradeRequired,
            Title ?? "HTTPS is required for equipment clients.",
            Detail),
        EquipmentClientAuthenticationFailureKind.ConfigurationUnavailable => Problem(
            StatusCodes.Status503ServiceUnavailable,
            Title ?? "Equipment client credentials are unavailable.",
            Detail),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Authentication failure is invalid."),
    };

    private static ObjectResult Problem(int status, string title, string? detail) => new(
        new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
        })
    {
        StatusCode = status,
    };
}

public sealed record EquipmentClientAuthenticationDecision(
    EquipmentClientIdentity? Identity,
    EquipmentClientAuthenticationRejection? Rejection)
{
    public static EquipmentClientAuthenticationDecision Authenticated(
        string clientId,
        string equipmentId) => new(new EquipmentClientIdentity(clientId, equipmentId), null);

    public static EquipmentClientAuthenticationDecision Rejected(
        EquipmentClientAuthenticationFailureKind kind,
        string? title = null,
        string? detail = null) => new(null, new(kind, title, detail));
}

/// <summary>
/// HTTPS, canonical client identity, equipment allow-list와 secret digest 검증을 숨기는 장비 인증 module입니다.
/// 호출자는 성공 identity 또는 이미 HTTP 의미가 고정된 rejection 하나만 처리합니다.
/// </summary>
public interface IEquipmentClientAuthenticator
{
    EquipmentClientAuthenticationDecision Authenticate(
        HttpRequest request,
        string? clientId,
        string? equipmentId);
}

public sealed class ConfigurationEquipmentClientAuthenticator : IEquipmentClientAuthenticator
{
    private readonly IConfiguration _configuration;

    public ConfigurationEquipmentClientAuthenticator(IConfiguration configuration)
        => _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public EquipmentClientAuthenticationDecision Authenticate(
        HttpRequest request,
        string? clientId,
        string? equipmentId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_configuration.GetValue(EquipmentClientAuthentication.RequireHttpsConfigurationKey, true)
            && !request.IsHttps)
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.HttpsRequired,
                "HTTPS is required for equipment clients.");
        }

        var clients = _configuration.GetSection(EquipmentClientAuthentication.ClientsConfigurationPath);
        if (!clients.GetChildren().Any())
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.ConfigurationUnavailable,
                "Run-admission client credentials are not configured.");
        }

        if (!TryNormalizeIdentifier(clientId, out var normalizedClientId)
            || !TryNormalizeIdentifier(equipmentId, out var normalizedEquipmentId))
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.InvalidRequest,
                "The equipment client request is invalid.",
                "ClientId and EquipmentId must be 1..100 characters without control characters or ':'.");
        }

        var client = clients.GetSection(normalizedClientId);
        if (!client.Exists())
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.Unauthorized);
        if (!TryReadExpectedDigest(client["SecretSha256"], out var expected))
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.ConfigurationUnavailable,
                "The equipment client digest is invalid.");
        }

        if (!request.Headers.TryGetValue(
                EquipmentClientAuthentication.ClientSecretHeader,
                out var suppliedValues)
            || suppliedValues.Count != 1
            || string.IsNullOrWhiteSpace(suppliedValues[0]))
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.Unauthorized);
        }

        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedValues[0]!));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.Unauthorized);

        // IConfiguration key lookup은 case-insensitive일 수 있으므로 canonical ID를 값으로 다시 검증합니다.
        var configuredClientId = client["ClientId"]?.Trim();
        var equipmentIds = client.GetSection("EquipmentIds")
            .GetChildren()
            .Select(static child => child.Value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Append(client["EquipmentId"]?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (string.IsNullOrWhiteSpace(configuredClientId) || equipmentIds.Length == 0)
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.ConfigurationUnavailable,
                "The equipment client identity or equipment allow-list is invalid.");
        }
        if (!string.Equals(configuredClientId, normalizedClientId, StringComparison.Ordinal)
            || !equipmentIds.Contains(normalizedEquipmentId, StringComparer.Ordinal))
        {
            return EquipmentClientAuthenticationDecision.Rejected(
                EquipmentClientAuthenticationFailureKind.Forbidden);
        }

        return EquipmentClientAuthenticationDecision.Authenticated(
            normalizedClientId,
            normalizedEquipmentId);
    }

    private static bool TryNormalizeIdentifier(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 100
               && !normalized.Contains(':')
               && normalized.All(static character => !char.IsControl(character));
    }

    private static bool TryReadExpectedDigest(string? value, out byte[] digest)
    {
        digest = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64)
            return false;
        try
        {
            digest = Convert.FromHexString(value.Trim());
            return digest.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
