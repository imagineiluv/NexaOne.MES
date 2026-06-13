using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexusFramework.Workflow.Runtime;
using NexusFramework.Workflow.Tooling;

namespace NexaOne.API.Controllers;

/// <summary>워크플로우 실행 API (§8) — NexusFramework WorkflowManager로 *.workflow를 실행한다.</summary>
[ApiController]
[Route("api/v1/workflow")]
[Authorize]
public class WorkflowController(WorkflowManager manager, IConfiguration config) : ControllerBase
{
    /// <summary>등록된 워크플로우(*.workflow) ID 목록.</summary>
    [HttpGet("list")]
    public IActionResult List()
    {
        var dir = ResolveDir(config);
        var ids = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.workflow")
                       .Select(Path.GetFileNameWithoutExtension)
                       .Where(id => id is not null)
                       .ToList()
            : new List<string?>();
        return Ok(ids);
    }

    /// <summary>워크플로우를 실행하고 노드별 상태/오류를 반환한다.</summary>
    [HttpPost("{workflowId}/execute")]
    public async Task<IActionResult> Execute(string workflowId, CancellationToken ct)
    {
        var path = Path.Combine(ResolveDir(config), $"{workflowId}.workflow");
        if (!System.IO.File.Exists(path))
            return NotFound($"Workflow '{workflowId}' not found.");

        // FlowExecutionOptions.Services로 DI 컨테이너를 노드(AssemblyInvocation)에 전달
        var options = new FlowExecutionOptions { Services = HttpContext.RequestServices, MaxParallelism = 4 };
        var reports = await manager.ExecuteAsync(path, options, ct);
        var report = reports.FirstOrDefault();
        if (report is null)
            return Ok(new { results = new Dictionary<string, string>() });

        return report.IsSuccessful
            ? Ok(new { results = report.NodeResults.ToDictionary(kv => kv.Key, kv => kv.Value.Status.ToString()) })
            : BadRequest(new { errors = report.Errors });
    }

    private static string ResolveDir(IConfiguration config)
        => config["Workflow:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "Config", "Workflow");
}
