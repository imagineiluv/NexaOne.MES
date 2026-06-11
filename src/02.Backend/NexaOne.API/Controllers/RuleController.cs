using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.API.Controllers.Models;
using NexaOne.Application.Messaging;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class RuleController : ControllerBase
{
    private readonly IRuleDispatcher _dispatcher;

    public RuleController(IRuleDispatcher dispatcher) => _dispatcher = dispatcher;

    [HttpPost("rule/{ruleName}")]
    public async Task<IActionResult> ExecuteRule(
        [FromRoute] string ruleName,
        [FromBody] RuleRequest request)
    {
        request.Head.RuleName = ruleName;
        var result = await _dispatcher.DispatchAsync(ruleName, request.Body ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [HttpPost("query")]
    public async Task<IActionResult> ExecuteQuery([FromBody] QueryRequest request)
    {
        var result = await _dispatcher.QueryAsync(request.Sql, request.Parameters ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [HttpPost("procedure")]
    public async Task<IActionResult> ExecuteProcedure([FromBody] ProcedureRequest request)
    {
        var result = await _dispatcher.ProcedureAsync(request.ProcedureName, request.Parameters ?? new());
        return Ok(new RuleResponse<object> { IsSuccess = true, Data = result });
    }

    [HttpPost("procedure/dataset")]
    public async Task<IActionResult> ExecuteProcedureDataSet([FromBody] ProcedureRequest request)
    {
        var result = await _dispatcher.ProcedureToDataSetAsync(request.ProcedureName, request.Parameters ?? new());
        return Ok(new RuleResponse<Dictionary<string, object>> { IsSuccess = true, Data = result });
    }
}
