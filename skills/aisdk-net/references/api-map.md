# Mapa de APIs de AiSdk.Net

| Tarea | Código |
|---|---|
| Texto | `await Ai.GenerateTextAsync(new() { Model = m, Prompt = "..." })` |
| Streaming | `Ai.StreamText(new() { ... }).TextStream` |
| Objeto | `(await Ai.GenerateObjectAsync<Receta>(new() { Model = m, Prompt = "..." })).Object` |
| Herramienta | `Tool.Create<Entrada, Salida>("nombre", "descripción", (input, ct) => ...)` o `Tool.FromDelegate(...)` |
| Agente | `new ToolLoopAgent(new AgentSettings { Model, Instructions, Tools, Skills })` |
| Memoria | `agent.CreateSession(conversationId, store)` |
| Proveedores | `AnthropicProvider.Default.Messages("claude-opus-5-5")`, `OpenAIProvider.Default.Responses("gpt-5")`, `GoogleProvider.Default.Gemini(...)` |
| Fallback | `modelo.WithFallback(otro)` |
| RAG | `InMemoryVectorStore`, `PgVectorStore`, `VectorDataStore`; `store.AsTool(topK, filter, hybrid)` |
| Documentos a Markdown | `new MarkdownConverter(new() { Ocr = new TesseractOcr() }).ConvertFileAsync(path)` |
| Word/PDF/Excel | `DocumentGenerator.ToDocx/ToPdf(markdown)`, `new SpreadsheetBuilder().AddSheet(...)`, `DocumentTools.Create(...)` |
| Artifacts | `ArtifactTools.Create(store)`, `app.MapArtifacts()` |
| Multi-agente | `Orchestrations.Parallel([...], aggregator)`, `.Router(...)`, `.Supervisor(...)`, `.Handoffs(...)`, `.EvaluatorOptimizer(...)` |
| Workflows | `Workflow.Create<TIn, TOut>(name, async (ctx, input) => ...)`, `services.AddAiSdkWorkflows(...)`, `app.MapWorkflows()` |
| Skills | `new SkillLibrary().LoadDirectory("skills")`, `AgentSettings.Skills`, `SkillAuthoringTools.Create(...)` |
| Skills de Anthropic | `AnthropicSkills.Options([AnthropicSkill.Excel])` + `AnthropicTools.CodeExecution()` |
| ASP.NET + useChat | `stream.ToUIMessageStreamResult()`, `UIMessageConverter.ToModelMessages(request.Messages)` |
| Pruebas | `MockLanguageModel`, `MockEmbeddingModel` |
