{
  "version": "1.0",
  "properties": { "id": "Echo", "name": "데모 Echo 워크플로우 (§8)" },
  "graph": {
    "nodes": [
      {
        "id": "echo",
        "type": "AssemblyInvocation",
        "properties": {
          "assemblyPath": "NexaOne.Application.dll",
          "typeName": "NexaOne.Application.Workflow.WorkflowDemoService",
          "methodName": "EchoAsync",
          "arguments": { "message": "hello workflow" }
        }
      },
      {
        "id": "upper",
        "type": "AssemblyInvocation",
        "properties": {
          "assemblyPath": "NexaOne.Application.dll",
          "typeName": "NexaOne.Application.Workflow.WorkflowDemoService",
          "methodName": "UppercaseAsync",
          "arguments": { "text": "{{message}}" }
        }
      }
    ],
    "edges": [
      { "id": "e1", "from": { "nodeId": "echo", "port": "out" }, "to": { "nodeId": "upper", "port": "in" } }
    ]
  }
}
