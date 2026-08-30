using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NexaOne.Server.Security;
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
    internal const string ClientSecretHeader = EquipmentClientAuthentication.ClientSecretHeader;

    private readonly IRunAdmissionService _service;
    private readonly IConfiguration _configuration;
    private readonly IEquipmentClientAuthenticator _clientAuthenticator;

    public RunAdmissionController(
        IRunAdmissionService service,
        IConfiguration configuration,
        IEquipmentClientAuthenticator clientAuthenticator)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clientAuthenticator = clientAuthenticator ?? throw new ArgumentNullException(nameof(clientAuthenticator));
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

        return _clientAuthenticator.Authenticate(Request, clientId, equipmentId).Rejection?.ToActionResult();
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

}
