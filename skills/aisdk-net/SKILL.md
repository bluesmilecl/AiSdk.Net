---
name: aisdk-net
description: >-
  Writes C# code with AiSdk.Net (AiSdk.Net.* packages, AiSdk.* namespaces): agents, tools,
  streaming with useChat, structured output, RAG, multi-agent orchestration, workflows, skills and
  document generation. USE FOR: "use AiSdk", "agent in .NET with AiSdk", Ai.GenerateTextAsync,
  ToolLoopAgent, Tool.Create, MapWorkflows, SkillLibrary, AnthropicProvider, build errors or
  unexpected behavior with these APIs. DO NOT USE FOR: Semantic Kernel, Microsoft.Extensions.AI without
  AiSdk, the official OpenAI or Anthropic .NET SDKs, or TypeScript code for the Vercel AI SDK.
  Spanish triggers: "usar AiSdk", "agente en .NET con AiSdk", "herramientas", "flujos de trabajo".
license: MIT
compatibility: Requires .NET 10 and the AiSdk.Net.* packages from NuGet or via project reference.
metadata:
  repository: bluesmilecl/sdk-agent-ia-net
---

# AiSdk.Net

Guide to writing correct AiSdk.Net code on the first try. It covers the decisions where a model
without this skill usually goes wrong: package names, the tool loop, provider options,
testing without an API key and the rules of durable workflows.

## When to Use

- Creating or modifying agents, tools, chat endpoints or workflows with AiSdk.Net.
- Fixing build errors or unexpected behavior with these APIs.

## When Not to Use

- Projects that use another AI SDK (Semantic Kernel, the official OpenAI/Anthropic SDKs).

## Workflow

1. Detect which packages the project uses (`dotnet list package` or the `.csproj` files). The packages are named
   `AiSdk.Net.*`, but the `using` directives are `AiSdk.*`:

   ```bash
   dotnet add package AiSdk.Net.Anthropic   # using AiSdk; using AiSdk.Anthropic;
   ```

2. Choose the API for the task (details in [references/api-map.md](references/api-map.md)):

   | Need | Use | Don't use |
   |---|---|---|
   | A single response | `Ai.GenerateTextAsync(new GenerateTextOptions { ... })` | raw `HttpClient` |
   | Tools over several steps | `StopWhen = StopCondition.StepCountIs(n)` | omitting `StopWhen` (stops after 1 step) |
   | Reusable agent | `ToolLoopAgent` or a class that inherits `Agent` | repeating options on every call |
   | Typed object | `Ai.GenerateObjectAsync<T>` | parsing JSON by hand |
   | Web chat with `useChat` | `agent.Stream(...).ToUIMessageStreamResult()` | hand-written SSE |
   | Several agents | `Orchestrations.Sequential/Parallel/Router/Supervisor/Handoffs` | nesting calls by hand |
   | Long-running processes or approvals | `AiSdk.Workflows` (`ctx.StepAsync`, `WaitForEventAsync`) | long `Task.Delay`s |

3. Provider-specific options go in `ProviderOptions`, created with the package helpers:

   ```csharp
   ProviderOptions = AnthropicOptions.Create(effort: "medium", refusalFallback: true),
   ```

4. For tests use `MockLanguageModel` (no API key or network); each `Returns*` answers one call, in order:

   ```csharp
   var model = new MockLanguageModel()
       .ReturnsToolCall("get_weather", new { city = "Santiago" })
       .ReturnsText("It's 20 °C.");
   ```

5. In Minimal APIs, a lambda whose only parameter is `HttpContext` is treated as a `RequestDelegate` and its
   `IResult` is discarded. Add another parameter (e.g. `CancellationToken ct`).

6. In workflows, every side effect (model, API, email) goes inside `ctx.StepAsync("name", ...)`:
   the code is re-executed on resume and completed steps return their saved result.

7. Build and run the tests. Report errors exactly as they appear; do not claim success without building.

## Validation

- [ ] `dotnet build` has no errors and the tests pass with `MockLanguageModel`.
- [ ] Agents with tools have `StopWhen` (or inherit the `Agent` default: 20 steps).
- [ ] No API key was left in the code (they are read from environment variables: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`...).

## Common Pitfalls

| Problem | Solution |
|---|---|
| `using AiSdk.Net` does not compile | The namespaces are `AiSdk`, `AiSdk.Anthropic`, `AiSdk.Workflows`... |
| The tool is called but there is no final answer | `StopWhen` is missing from `GenerateTextOptions` |
| `ArgumentException` for a path in a skill | Skill file paths are relative and cannot contain `..` |
| Skill scripts do not run | They require `SkillToolOptions.ScriptRunner` and skills with `Trusted = true` |
| The model does not activate a skill | Improve the description: what it does, USE FOR and DO NOT USE FOR |
