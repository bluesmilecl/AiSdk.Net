# Equivalencias (Vercel AI SDK, Laravel AI)

> 🇬🇧 [Read in English](Feature-Map)

Cómo se corresponden los conceptos del AI SDK de Vercel (TypeScript) y del Laravel AI SDK con AiSdk (.NET).

## Equivalencias

| AI SDK (TypeScript) | Laravel AI SDK | AiSdk (.NET) |
|---|---|---|
| `generateText({ model, prompt })` | `Ai::text()` / `$agent->prompt()` | `await Ai.GenerateTextAsync(new() { Model, Prompt })` |
| `streamText(...)` → `textStream` | `->stream()` | `Ai.StreamText(...)` → `TextStream`, `FullStream` |
| `generateObject({ schema: z.object(...) })` | `HasStructuredOutput` | `Ai.GenerateObjectAsync<Recipe>(...)` |
| `streamObject` → `partialObjectStream` | — | `Ai.StreamObject<T>(...).PartialObjectStream` |
| `tool({ description, inputSchema, execute })` | `Tool` class | `Tool.Create<TIn, TOut>(...)` / `Tool.FromDelegate(...)` |
| `stopWhen: stepCountIs(5)` | `maxSteps` | `StopWhen = StopCondition.StepCountIs(5) \| StopCondition.HasToolCall("done")` |
| `prepareStep`, `onStepFinish` | — | `PrepareStep`, `OnStepFinish` |
| `new ToolLoopAgent({...})` | `class X extends Agent` | `new ToolLoopAgent(new AgentSettings {...})` o `class X : Agent` |
| `Output.object / array / choice` | — | `Output.Object<T>()`, `Output.Array<T>()`, `Output.Choice(...)` |
| `needsApproval` | — | `tool.RequireApproval()` + `OnToolApproval` |
| `RemembersConversations` | `RemembersConversations` | `agent.CreateSession(id, store)` + `IConversationStore` |
| `wrapLanguageModel({ middleware })` | — | `model.WithMiddleware(...)` |
| `createProviderRegistry` | `config/ai.php` | `ProviderRegistry` / `AddAiSdk(...)` |
| `embed`, `embedMany`, `cosineSimilarity` | `Embeddings` | `Ai.EmbedAsync`, `Ai.EmbedManyAsync`, `Ai.CosineSimilarity` |
| `toUIMessageStreamResponse()` + `useChat` | — | `stream.ToUIMessageStreamResult()` + `UIMessageConverter` |
| `MockLanguageModelV2` | `Ai::fake()` | `MockLanguageModel` |
| `experimental_createMCPClient` | — | `McpClient.ConnectAsync(...)` + `GetToolsAsync()` |
| `anthropic.tools.webSearch_*`, `openai.tools.webSearch` | provider tools | `AnthropicTools.WebSearch()`, `OpenAITools.WebSearch()` |
| `result.sources` | — | `result.Sources` |
| `openai.responses("gpt-5")` | — | `openai.Responses("gpt-5")` |
| — | failover (`provider: [...]`) | `model.WithFallback(...)` / `registry.Fallback(...)` |
| `experimental_repairToolCall` | — | `RepairToolCall = ToolCallRepair.WithModel(...)` |
| `onChunk`, `onError`, `onFinish`, `onAbort` | — | `OnChunk`, `OnError`, `OnFinish`, `OnAbort` |
| `experimental_generateImage` | `Image::of()` | `Ai.GenerateImageAsync(...)` |
| `experimental_generateSpeech` | `Audio::of()` | `Ai.GenerateSpeechAsync(...)` |
| `experimental_transcribe` | `Transcription::of()` | `Ai.TranscribeAsync(...)` |
| `rerank` | `Reranking` | `Ai.RerankAsync(...)` |
| — | `Stores` / `Files` | `InMemoryVectorStore`, `IVectorStore`, `provider.Files`, `openai.VectorStores` |
| `smoothStream()` | — | `StreamTransform = StreamTransforms.Smooth()` |
| `extractReasoningMiddleware` | — | `Middlewares.ExtractReasoning()` |
| `createUIMessageStream` + `writer.write({ type: 'data-*' })` | — | `UIMessageStreamResults.Create(writer => ...)`, `ctx.WriteData(...)` |
| `resumable-stream` + `useChat({ resume })` | — | `ToResumableUIMessageStreamResult(store, chatId)` + `UIMessageStreamResults.Resume` |
| patrones de agentes (sequential, parallel, routing, orchestrator-worker, evaluator-optimizer) | — | `Orchestrations.Sequential/Parallel/Router/Supervisor/EvaluatorOptimizer` |
| handoffs (OpenAI Agents SDK) | — | `Orchestrations.Handoffs(...)` |
| Workflow DevKit (`"use workflow"`, `"use step"`, `sleep`, hooks) | queues / jobs | `Workflow.Create(...)`, `ctx.StepAsync`, `ctx.SleepAsync`, `ctx.WaitForEventAsync` |

> [!TIP]
> ¿Vienes de TypeScript? La mayoría de los nombres son los mismos en PascalCase (los métodos awaitables terminan en `Async`), y los objetos de opciones pasan a ser `new XxxOptions { ... }`.

---

**Siguiente:** [Arquitectura](es-Arquitectura) ➡️
