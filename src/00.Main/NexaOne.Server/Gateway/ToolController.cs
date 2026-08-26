using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.Server.Gateway;

[ApiController]
[Route("api/v1/ems/tools")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class ToolController : ControllerBase
{
    private readonly IToolBridge _bridge;
    public ToolController(IToolBridge bridge) => _bridge = bridge;

    [HttpPost]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> Save([FromBody] ToolCommand command, CancellationToken ct)
        => await WithActor(command, (c, token) => _bridge.SaveAsync(c, token), ct);

    [HttpPost("mount")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> Mount([FromBody] ToolMountCommand command, CancellationToken ct)
        => await WithActor(command, (c, token) => _bridge.MountAsync(c, token), ct);

    [HttpPost("unmount")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> Unmount([FromBody] ToolUnmountCommand command, CancellationToken ct)
        => await WithActor(command, (c, token) => _bridge.UnmountAsync(c, token), ct);

    [HttpPost("usage")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> RecordUsage([FromBody] ToolUsageCommand command, CancellationToken ct)
        => await WithActor(command, (c, token) => _bridge.RecordUsageAsync(c, token), ct);

    [HttpPost("inspection")]
    [RequirePermission(Permissions.EmsManage)]
    public async Task<IActionResult> RecordInspection([FromBody] ToolInspectionCommand command, CancellationToken ct)
        => await WithActor(command, (c, token) => _bridge.RecordInspectionAsync(c, token), ct);

    private async Task<IActionResult> WithActor<TCommand, TResult>(
        TCommand command,
        Func<TCommand, CancellationToken, Task<Result<TResult>>> action,
        CancellationToken ct)
    {
        var actor = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        object secured = command switch
        {
            ToolCommand c => (object)(c with { ActorId = actor }),
            ToolMountCommand c => c with { ActorId = actor },
            ToolUnmountCommand c => c with { ActorId = actor },
            ToolUsageCommand c => c with { ActorId = actor },
            ToolInspectionCommand c => c with { ActorId = actor },
            _ => throw new InvalidOperationException("Unsupported tool command."),
        };
        return (await action((TCommand)(object)secured, ct)).ToActionResult();
    }
}
