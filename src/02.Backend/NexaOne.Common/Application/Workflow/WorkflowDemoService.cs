using NexaFramework.Workflow.Reflection;

namespace NexaOne.Application.Workflow;

/// <summary>
/// 워크플로우 엔진 연계 데모 (§8). <see cref="WorkflowCallableAttribute"/>로 표시된 메서드는
/// NexaFramework <c>AssemblyInvocationNode</c>가 *.workflow의 노드로 리플렉션 호출한다.
/// 실제 업무 워크플로우(레시피 승인 등)는 이 패턴으로 도메인 서비스 메서드를 노드화하여 구성한다.
/// </summary>
public sealed class WorkflowDemoService
{
    [WorkflowCallable("Echo", Category = "Demo", Description = "입력 메시지를 그대로 반환하는 데모 노드")]
    public Task<object> EchoAsync(string message)
        => Task.FromResult<object>(new { echoed = message });

    [WorkflowCallable("Uppercase", Category = "Demo", Description = "입력을 대문자로 변환")]
    public Task<object> UppercaseAsync(string text)
        => Task.FromResult<object>(new { result = (text ?? string.Empty).ToUpperInvariant() });
}
