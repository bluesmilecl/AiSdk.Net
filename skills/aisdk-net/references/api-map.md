# AiSdk.Net API map

| Task | Code |
|---|---|
| Text | `await Ai.GenerateTextAsync(new() { Model = m, Prompt = "..." })` |
| Streaming | `Ai.StreamText(new() { ... }).TextStream` |
| Object | `(await Ai.GenerateObjectAsync<Recipe>(new() { Model = m, Prompt = "..." })).Object` |
| Tool | `Tool.Create<Input, Output>("name", "description", (input, ct) => ...)` or `Tool.FromDelegate(...)` |
| Agent | `new ToolLoopAgent(new AgentSettings { Model, Instructions, Tools, Skills })` |
| Memory | `agent.CreateSession(conversationId, store)` |
| Providers | `AnthropicProvider.Default.Messages("claude-opus-5-5")`, `OpenAIProvider.Default.Responses("gpt-5")`, `GoogleProvider.Default.Gemini(...)` |
| Fallback | `model.WithFallback(other)` |
| RAG | `InMemoryVectorStore`, `PgVectorStore`, `VectorDataStore`; `store.AsTool(topK, filter, hybrid)` |
| Documents to Markdown | `new MarkdownConverter(new() { Ocr = new TesseractOcr() }).ConvertFileAsync(path)` |
| Word/PDF/Excel | `DocumentGenerator.ToDocx/ToPdf(markdown)`, `new SpreadsheetBuilder().AddSheet(...)`, `DocumentTools.Create(...)` |
| Artifacts | `ArtifactTools.Create(store)`, `app.MapArtifacts()` |
| Multi-agent | `Orchestrations.Parallel([...], aggregator)`, `.Router(...)`, `.Supervisor(...)`, `.Handoffs(...)`, `.EvaluatorOptimizer(...)` |
| Workflows | `Workflow.Create<TIn, TOut>(name, async (ctx, input) => ...)`, `services.AddAiSdkWorkflows(...)`, `app.MapWorkflows()` |
| Skills | `new SkillLibrary().LoadDirectory("skills")`, `AgentSettings.Skills`, `SkillAuthoringTools.Create(...)` |
| Anthropic Skills | `AnthropicSkills.Options([AnthropicSkill.Excel])` + `AnthropicTools.CodeExecution()` |
| ASP.NET + useChat | `stream.ToUIMessageStreamResult()`, `UIMessageConverter.ToModelMessages(request.Messages)` |
| Testing | `MockLanguageModel`, `MockEmbeddingModel` |
