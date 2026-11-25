# Feature Map (Vercel AI SDK, Laravel AI)

> 🇪🇸 [Leer en español](es-Equivalencias)

How the concepts of the Vercel AI SDK (TypeScript) and the Laravel AI SDK map to AiSdk (.NET).

## Equivalents

| AI SDK (TypeScript) | Laravel AI SDK | AiSdk (.NET) |
|---|---|---|
| `generateText({ model, prompt })` | `Ai::text()` / `$agent->prompt()` | `await Ai.GenerateTextAsync(new() { Model, Prompt })` |
| `streamText(...)` → `textStream` | `->stream()` | `Ai.StreamText(...)` → `TextStream`, `FullStream` |
| `generateObject({ schema: z.object(...) })` | `HasStructuredOutput` | `Ai.GenerateObjectAsync<Recipe>(...)` |
| `streamObject` → `partialObjectStream` | — | `Ai.StreamObject<T>(...).PartialObjectStream` |
| `tool({ description, inputSchema, execute })` | `Tool` class | `Tool.Create<TIn, TOut>(...)` / `Tool.FromDelegate(...)` |
| `stopWhen: stepCountIs(5)` | `maxSteps` | `StopWhen = StopCondition.StepCountIs(5) \| StopCondition.HasToolCall("done")` |
| `prepareStep`, `onStepFinish` | — | `PrepareStep`, `OnStepFinish` |
| `new ToolLoopAgent({...})` | `class X extends Agent` | `new ToolLoopAgent(new AgentSettings {...})` or `class X : Agent` |
| `Output.object / array / choice` | — | `Output.Object<T>()`, `Output.Array<T>()`, `Output.Choice(...)` |
| `needsApproval` | — | `tool.RequireApproval()` + `OnToolApproval` |
| `tool-approval-request` / `tool-approval-response` | — | `DeferToolApprovals` + `result.PendingApprovals`, then `ToolApprovals` (or `session.SendApprovalsAsync`) |
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
| agent patterns (sequential, parallel, routing, orchestrator-worker, evaluator-optimizer) | — | `Orchestrations.Sequential/Parallel/Router/Supervisor/EvaluatorOptimizer` |
| handoffs (OpenAI Agents SDK) | — | `Orchestrations.Handoffs(...)` |
| Workflow DevKit (`"use workflow"`, `"use step"`, `sleep`, hooks) | queues / jobs | `Workflow.Create(...)`, `ctx.StepAsync`, `ctx.SleepAsync`, `ctx.WaitForEventAsync` |

> [!TIP]
> Coming from TypeScript? Most names are the same in PascalCase (awaitable methods end in `Async`), and options objects become `new XxxOptions { ... }`.

---

**Next:** [Architecture](Architecture) ➡️
