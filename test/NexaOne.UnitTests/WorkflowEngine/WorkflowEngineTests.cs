using System.Reflection;
using NexaOne.Application.Workflow;
using NexaFramework.Workflow.Nodes;
using NexaFramework.Workflow.Reflection;
using NexaFramework.Workflow.Tooling;

namespace NexaOne.UnitTests.WorkflowEngine;

/// <summary>§8 — NexaFramework 워크플로우 엔진의 앱 연계 구성(NodeRegistry·WorkflowManager)과
/// [WorkflowCallable] 노드 표시를 검증한다. 실제 *.workflow 실행은 통합 테스트 영역.</summary>
public sealed class WorkflowEngineTests
{
    [Fact]
    public void NodeRegistry_creates_assembly_invocation_node()
    {
        var registry = new NodeRegistry();
        registry.RegisterAssemblyInvocationNode();

        registry.TryCreate(AssemblyInvocationNode.NodeType, "n1", out var node).Should().BeTrue();
        node.Should().NotBeNull();
    }

    [Fact]
    public void NodeRegistry_without_assembly_node_cannot_create_it()
    {
        var registry = new NodeRegistry();   // RegisterAssemblyInvocationNode 미호출

        registry.TryCreate(AssemblyInvocationNode.NodeType, "n1", out var node).Should().BeFalse();
        node.Should().BeNull();
    }

    [Fact]
    public void WorkflowManager_constructs_with_single_executor()
    {
        var registry = new NodeRegistry();
        registry.RegisterAssemblyInvocationNode();
        var executor = new WorkflowExecutor(registry);

        using var manager = new WorkflowManager(executor);

        manager.Executors.Should().ContainSingle("DI 구성과 동일하게 단일 Executor로 생성된다");
    }

    [Fact]
    public void WorkflowDemoService_methods_are_marked_workflow_callable()
    {
        var callable = typeof(WorkflowDemoService).GetMethods()
            .Where(m => m.GetCustomAttribute<WorkflowCallableAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        callable.Should().Contain(new[] { "EchoAsync", "UppercaseAsync" },
            "[WorkflowCallable] 메서드는 AssemblyInvocationNode가 워크플로우 노드로 호출한다");
    }
}
