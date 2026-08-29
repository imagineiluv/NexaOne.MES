using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 설비 client 전용 자동운전 lease endpoint입니다. 대화형 사용자 JWT와 섞지 않고 설치별 shared secret을
/// SHA-256 digest로 구성하며, lease/access token은 FDC module이 소유합니다. 운영 기본은 HTTPS 필수입니다.
/// </summary>
[ApiController]
[Route("api/v1/run-admission")]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RunAdmissionController : ControllerBase
{
    internal const string ClientSecretHeader = "X-Nexa-Run-Client-Secret";

    private readonly IRunAdmissionService _service;
    private readonly IConfiguration _configuration;

    public RunAdmissionController(
        IRunAdmissionService service,
        IConfiguration configuration)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    [HttpPost("acquire")]
    [ProducesResponseType<RunAdmissionDecisionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status426UpgradeRequired)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Acquire(
        [FromBody] RunAdmissionAcquireDto request,
        CancellationToken ct)
    {
        var rejected = Authenticate(request?.ClientId, request?.EquipmentId);
        if (rejected is not null) return rejected;
        try
        {
            return Ok(await _service.AcquireAsync(request!, ct));
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }
    }

    [HttpPost("keep-alive")]
    [ProducesResponseType<RunAdmissionStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status426UpgradeRequired)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> KeepAlive(
        [FromBody] RunAdmissionLeaseProofDto request,
        CancellationToken ct)
    {
        var rejected = Authenticate(request?.ClientId, request?.EquipmentId);
        if (rejected is not null) return rejected;
        try
        {
            return Ok(await _service.KeepAliveAsync(request!, ct));
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }
    }

    [HttpPost("release")]
    [ProducesResponseType<RunAdmissionReleaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status426UpgradeRequired)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Release(
        [FromBody] RunAdmissionLeaseProofDto request,
        CancellationToken ct)
    {
        var rejected = Authenticate(request?.ClientId, request?.EquipmentId);
        if (rejected is not null) return rejected;
        try
        {
            return Ok(await _service.ReleaseAsync(request!, ct));
        }
        catch (ArgumentException exception)
        {
            return InvalidRequest(exception);
        }
    }

    private IActionResult? Authenticate(string? clientId, string? equipmentId)
    {
        if (!_configuration.GetValue("RunAdmission:Enabled", false))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Run admission is disabled.",
                    Detail = "A durable shared admission ledger is required before this endpoint can be enabled.",
                });
        }

        if (_configuration.GetValue("RunAdmission:RequireHttps", true) && !Request.IsHttps)
        {
            return StatusCode(
                StatusCodes.Status426UpgradeRequired,
                new ProblemDetails
                {
                    Status = StatusCodes.Status426UpgradeRequired,
                    Title = "HTTPS is required for run admission.",
                });
        }

        var clients = _configuration.GetSection("RunAdmission:Clients");
        if (!clients.GetChildren().Any())
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Run-admission client credentials are not configured.",
                });
        }

        if (!TryNormalizeIdentifier(clientId, out var normalizedClientId)
            || !TryNormalizeIdentifier(equipmentId, out var normalizedEquipmentId))
            return InvalidRequest("ClientId and EquipmentId must be 1..100 characters without control characters or ':'.");

        var client = clients.GetSection(normalizedClientId);
        if (!client.Exists())
            return Unauthorized();
        if (!TryReadExpectedDigest(client["SecretSha256"], out var expected))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "The run-admission client digest is invalid.",
                });
        }

        if (!Request.Headers.TryGetValue(ClientSecretHeader, out var suppliedValues)
            || suppliedValues.Count != 1
            || string.IsNullOrWhiteSpace(suppliedValues[0]))
        {
            return Unauthorized();
        }

        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedValues[0]!));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return Unauthorized();

        // IConfiguration key lookup is case-insensitive일 수 있으므로 canonical client id도 값으로 고정한다.
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
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "The run-admission client identity or equipment allow-list is invalid.",
                });
        }
        if (!string.Equals(configuredClientId, normalizedClientId, StringComparison.Ordinal)
            || !equipmentIds.Contains(normalizedEquipmentId, StringComparer.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private BadRequestObjectResult InvalidRequest(ArgumentException exception) =>
        InvalidRequest(exception.Message);

    private BadRequestObjectResult InvalidRequest(string detail) =>
        BadRequest(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The run-admission request is invalid.",
            Detail = detail,
        });

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
