using System.Text.RegularExpressions;
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

    // 워크플로우 ID 허용 문자(영숫자·하이픈·언더스코어) — 경로 조작(../, 절대경로, 백슬래시) 차단
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>워크플로우를 실행하고 노드별 상태/오류를 반환한다.</summary>
    // 워크플로우 실행은 등록된 어셈블리 호출 노드를 구동하므로 인증만으로는 부족하다 — 실행 권한을 요구한다(기본 ADMIN).
    [HttpPost("{workflowId}/execute")]
    [Authorize(Policy = "perm:workflow:execute")]
    public async Task<IActionResult> Execute(string workflowId, CancellationToken ct)
    {
        // 라우트 파라미터를 파일 경로에 쓰기 전에 화이트리스트로 검증 (경로 조작 방지)
        if (string.IsNullOrEmpty(workflowId) || workflowId.Length > 128 || !IdPattern.IsMatch(workflowId))
            return BadRequest("Invalid workflow id.");

        var dir = ResolveDir(config);
        var path = Path.Combine(dir, $"{workflowId}.workflow");

        // 정규화된 최종 경로가 워크플로우 디렉터리 하위인지 재확인 (심층 방어)
        var baseFull = Path.GetFullPath(dir);
        var pathFull = Path.GetFullPath(path);
        if (!pathFull.StartsWith(
                baseFull.EndsWith(Path.DirectorySeparatorChar) ? baseFull : baseFull + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return BadRequest("Invalid workflow id.");

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
