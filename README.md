# AiSdk para .NET

SDK de IA y agentes para **.NET 10**, inspirado en el [AI SDK de Vercel](https://ai-sdk.dev/docs/agents/overview)
y en el [AI SDK de Laravel](https://laravel.com/docs/ai-sdk). Una sola API, independiente del proveedor, para:

- generar texto y hacer streaming (`GenerateTextAsync`, `StreamText`)
- generar objetos tipados con JSON Schema a partir de tus clases (`GenerateObjectAsync<T>`, `StreamObject<T>`)
- herramientas (*tools*) con esquema automático y bucle de agente (`StopWhen`, `PrepareStep`, `OnStepFinish`)
- agentes (`ToolLoopAgent` o clases que heredan de `Agent`), memoria de conversación y sub-agentes
- **orquestación multi-agente**: cadena, paralelo con agregador, router, supervisor, traspasos (handoffs) y evaluador-optimizador
- **artifacts como Claude**: los agentes crean y editan páginas HTML, componentes React, documentos, código y diagramas versionados
- **generación de archivos**: Word, PDF y Excel con estilos profesionales, directamente o como herramientas del agente
- **workflows durables y automatizaciones**: pasos con reintentos, esperas, aprobaciones humanas, cron, eventos y webhooks;
  los agentes pueden programar sus propias tareas
- aprobación humana de herramientas (*human-in-the-loop*)
- middleware de modelos, registro de proveedores con alias, reintentos y trazas OpenTelemetry
- embeddings y similitud coseno
- **cliente MCP** (stdio y HTTP): usa las herramientas de cualquier servidor MCP en tus agentes
- **herramientas del proveedor**: búsqueda web, web fetch, ejecución de código, búsqueda en archivos, con **fuentes/citas**
- **fallback entre proveedores**, **reparación** de llamadas a herramientas inválidas y callbacks `OnChunk`/`OnError`/`OnFinish`/`OnAbort`
- **imágenes, voz y transcripción** (`GenerateImageAsync`, `GenerateSpeechAsync`, `TranscribeAsync`)
- **RAG**: almacén vectorial, filtros por metadatos, búsqueda híbrida, reranking y búsqueda como herramienta del agente;
  **PostgreSQL + pgvector** nativo y adaptador para **Qdrant, Pinecone, Azure AI Search, Redis, Weaviate, Mongo…**
  (Microsoft.Extensions.VectorData)
- **documentos a Markdown**: PDF, Word, Excel, PowerPoint, HTML, imágenes, audio y video, con **OCR** (Tesseract local,
  modelos con visión o Mistral OCR), para dar contexto a la IA y usar **modelos sin visión** con cualquier adjunto
- **compresión de contexto**: resumen incremental de conversaciones largas, compresión extractiva por relevancia y map-reduce
- **conteo de tokens**, **batches** (a mitad de precio), **Files API** y **vector stores** de los proveedores
- backend ASP.NET Core compatible con `useChat` de AI SDK UI (React, Vue, Svelte), con **datos propios** y **streams reanudables**

**Proveedores:** OpenAI (y compatibles: Ollama, Groq, DeepSeek, OpenRouter…), Anthropic Claude, Google Gemini,
Mistral, Amazon Bedrock, Cohere, y cualquier proveedor de Microsoft.Extensions.AI.

## Instalación

```bash
dotnet add package AiSdk.Net               # núcleo
dotnet add package AiSdk.Net.Anthropic     # o AiSdk.Net.OpenAI, .Google, .Mistral, .Bedrock, .Cohere
dotnet add package AiSdk.Net.Mcp           # cliente MCP
dotnet add package AiSdk.Net.AspNetCore    # backend para useChat
dotnet add package AiSdk.Net.Documents     # leer (PDF/Office/imágenes/audio/video → Markdown + OCR) y generar Word/PDF/Excel
dotnet add package AiSdk.Net.Workflows     # workflows durables, triggers y automatizaciones
dotnet add package AiSdk.Net.PgVector      # almacén PostgreSQL + pgvector
dotnet add package AiSdk.Net.VectorData    # Qdrant, Pinecone, Azure AI Search… vía Microsoft.Extensions.VectorData
```

Los paquetes se llaman `AiSdk.Net.*` (el id `AiSdk` ya existe en nuget.org); los namespaces son `AiSdk.*`.
Se publican con el workflow `release.yml` al crear un tag `vX.Y.Z` (requiere el secreto `NUGET_API_KEY`).

| Paquete | Contenido |
|---|---|
| `AiSdk` | Núcleo: API `Ai.*`, tools, agentes, orquestaciones multi-agente, artifacts, `Output`, middleware, registro, embeddings, UI stream, `MockLanguageModel` |
| `AiSdk.OpenAI` | OpenAI: API Responses y Chat Completions, herramientas de servidor, embeddings, imágenes, voz y transcripción. `Chat` funciona con servidores compatibles (Ollama, Groq, DeepSeek, OpenRouter, LM Studio, vLLM…) |
| `AiSdk.Anthropic` | Claude (Messages API): tools, streaming, razonamiento con firma, salida estructurada, herramientas de servidor (web search, web fetch, code execution), citas, caché de prompts, fallback ante rechazos |
| `AiSdk.Google` | Gemini: tools, razonamiento con firmas, Google Search con fuentes, ejecución de código, URL context, embeddings, conteo de tokens e Imagen |
| `AiSdk.Mistral` | Mistral AI: chat, razonamiento de Magistral, tools, salida estructurada y embeddings |
| `AiSdk.Bedrock` | Amazon Bedrock (API Converse): Claude, Llama, Nova, Mistral… con SigV4 o API key; embeddings Titan y Cohere |
| `AiSdk.Cohere` | Reranking (Rerank) y embeddings (Embed) |
| `AiSdk.Mcp` | Cliente MCP (Model Context Protocol): herramientas, recursos, prompts, roots, sampling y elicitation; stdio y Streamable HTTP |
| `AiSdk.Extensions.AI` | Adaptador de `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) → cualquier proveedor de ese ecosistema |
| `AiSdk.Extensions.DependencyInjection` | `services.AddAiSdk(...)`, `IHttpClientFactory`, agentes en el contenedor |
| `AiSdk.AspNetCore` | `ToUIMessageStreamResult()` / `ToTextStreamResult()`, `MapArtifacts()` y `MapWorkflows()` para Minimal APIs |
| `AiSdk.Documents` | `MarkdownConverter` (PDF, Word, Excel, PowerPoint, HTML, CSV/JSON, imágenes, audio, video), OCR con Tesseract, middleware `ConvertAttachments` para modelos sin visión; generación de Word, PDF y Excel (`DocxWriter`, `PdfWriter`, `SpreadsheetBuilder`, `DocumentTools`) |
| `AiSdk.Workflows` | `WorkflowEngine`: pasos durables, `SleepAsync`, `WaitForEventAsync`, reintentos; automatizaciones por cron, intervalo, fecha, evento o webhook; `AgentTasks` para que los agentes programen tareas; `AddAiSdkWorkflows()` |
| `AiSdk.PgVector` | Almacén PostgreSQL + pgvector: HNSW, filtros en `jsonb`, búsqueda híbrida (vectores + texto completo con RRF) |
| `AiSdk.VectorData` | `VectorDataStore`: cualquier conector de Microsoft.Extensions.VectorData (Qdrant, Pinecone, Azure AI Search, Redis, Weaviate, MongoDB, Cosmos DB, SQL Server, Elasticsearch…) |

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

## Inicio rápido

```bash
dotnet build
dotnet test
# ejemplos (sin API key):
dotnet run --project samples/QuickStart -- mock agent
# con Claude o OpenAI:
export ANTHROPIC_API_KEY=...   # o OPENAI_API_KEY
dotnet run --project samples/QuickStart -- anthropic tools
dotnet run --project samples/QuickStart -- openai stream-object
```

Ejemplos disponibles: `text`, `stream`, `tools`, `agent`, `object`, `stream-object`, `session`, `subagents`,
`web-search`, `fallback`, `repair`, `mcp`, `image`, `speech`, `transcribe`, `rag`, `count-tokens`, `batch`,
`document`, `attachments`, `compress`, `orchestrate`, `artifact`, `generate-docs`, `workflow`.

```bash
# MCP con cualquier servidor (stdio):
dotnet run --project samples/QuickStart -- anthropic mcp npx -y @modelcontextprotocol/server-everything
# Cualquier documento a Markdown (PDF, Word, Excel, PowerPoint, imagen, audio, video):
dotnet run --project samples/QuickStart -- anthropic document informe.pdf
# Modelo sin visión leyendo una boleta escaneada (OCR local):
dotnet run --project samples/QuickStart -- mistral attachments boleta.png
```

### Generar texto

```csharp
using AiSdk;
using AiSdk.Anthropic;

var model = AnthropicProvider.Default.Messages("claude-opus-5-5"); // ANTHROPIC_API_KEY

var result = await Ai.GenerateTextAsync(new GenerateTextOptions
{
    Model = model,
    System = "Responde en español.",
    Prompt = "¿Qué es .NET?",
    ProviderOptions = AnthropicOptions.Create(effort: "medium", refusalFallback: true),
});

Console.WriteLine(result.Text);
Console.WriteLine(result.TotalUsage.OutputTokens);
```

Cambiar de proveedor es cambiar una línea:

```csharp
using AiSdk.OpenAI;
var model = OpenAIProvider.Default.Chat("gpt-5");                                   // OPENAI_API_KEY
var local = OpenAIProvider.Compatible("ollama", "http://localhost:11434/v1").Chat("llama3.2");
```

### Streaming

```csharp
var stream = Ai.StreamText(new GenerateTextOptions { Model = model, Prompt = "Un haiku sobre C#" });

await foreach (var chunk in stream.TextStream)
    Console.Write(chunk);

var usage = await stream.TotalUsage;   // también: Text, Steps, FinishReason, ResponseMessages
```

`FullStream` expone todas las partes: `TextDeltaPart`, `ReasoningDeltaPart`, `ToolInputStartPart`,
`ToolCallStreamPart`, `ToolResultStreamPart`, `StartStepPart`/`FinishStepPart`, `FinishPart`, `ErrorPart`…
Los streams pueden consumirse varias veces y por varios consumidores.

### Herramientas y bucle de agente

```csharp
public sealed record WeatherQuery([property: Description("Ciudad")] string City);
public sealed record Weather(string City, int TemperatureC);

var weather = Tool.Create<WeatherQuery, Weather>(
    "get_weather", "Obtiene el clima de una ciudad",
    async (q, ct) => await weatherService.GetAsync(q.City, ct));

// O desde cualquier delegado/método; CancellationToken, ToolExecutionContext e IServiceProvider se inyectan.
var convert = Tool.FromDelegate("c_to_f", "Convierte °C a °F",
    ([Description("Temperatura en °C")] double c) => c * 9 / 5 + 32);

var result = await Ai.GenerateTextAsync(new GenerateTextOptions
{
    Model = model,
    Prompt = "¿Qué temperatura hace en Santiago, en °F?",
    Tools = [weather, convert],
    StopWhen = StopCondition.StepCountIs(5),
    OnStepFinish = step => { Console.WriteLine(step.ToolCalls.Count); return ValueTask.CompletedTask; },
});
```

Los errores de las herramientas (excepciones, JSON inválido, herramienta desconocida) se devuelven al modelo
como resultados con `IsError = true`, para que pueda corregirse. Las herramientas sin `Execute`
(`Tool.Define<T>`) se resuelven en el cliente: el bucle se detiene y quedan en `result.PendingToolCalls`.

### Agentes

**Por configuración** (estilo AI SDK):

```csharp
var agent = new ToolLoopAgent(new AgentSettings
{
    Model = model,
    Instructions = "Eres un asistente meteorológico.",
    Tools = [weather, convert],
    // StopWhen por defecto: StepCountIs(20)
});

var result = await agent.GenerateAsync("Compara Santiago y Lima");
var stream = agent.Stream("Compara Santiago y Lima");
```

**Por clase** (estilo Laravel), ideal con inyección de dependencias:

```csharp
public sealed class SupportAgent(ILanguageModel model, IOrderService orders) : Agent
{
    protected override ILanguageModel Model => model;
    protected override string Instructions => "Eres el agente de soporte de la tienda.";
    protected override IEnumerable<Tool> Tools =>
    [
        Tool.FromDelegate("lookup_order", "Busca un pedido",
            ([Description("Id del pedido")] string id, CancellationToken ct) => orders.FindAsync(id, ct)),
    ];
    protected override StopCondition StopWhen => StopCondition.StepCountIs(10);
}
```

**Memoria de conversación:**

```csharp
var chat = agent.CreateSession("conversacion-123", store);   // IConversationStore (en memoria por defecto)
await chat.SendAsync("Me llamo Ana");
var r = await chat.SendAsync("¿Cómo me llamo?");
```

`ModelMessage` es serializable a JSON (con discriminador `type`), así que implementar
`IConversationStore` sobre EF Core, Redis o Cosmos DB es directo.

**Sub-agentes / multi-agente:**

```csharp
var orchestrator = new ToolLoopAgent(new AgentSettings
{
    Model = model,
    Tools = [researchAgent.AsTool(), writerAgent.AsTool("writer", "Redacta el informe final")],
});
```

**Human-in-the-loop:**

```csharp
var deleteUser = Tool.Create<DeleteInput, string>("delete_user", "Elimina un usuario", Delete).RequireApproval();

await Ai.GenerateTextAsync(new GenerateTextOptions
{
    Model = model, Prompt = "...", Tools = [deleteUser], StopWhen = StopCondition.StepCountIs(5),
    OnToolApproval = async req => await AskOperatorAsync(req.ToolCall),   // false → se informa al modelo
});
```

**Control por paso** (`PrepareStep`): cambiar de modelo, forzar herramientas o recortar el contexto.

```csharp
PrepareStep = ctx => ValueTask.FromResult<PrepareStepResult?>(ctx.StepNumber switch
{
    0 => new() { ActiveTools = ["search"] },
    > 5 => new() { Messages = ctx.Messages.TakeLast(10).ToList() },
    _ => null,
}),
```

### Orquestación multi-agente

Patrones listos (ver *Building effective agents* de Anthropic). Todos los agentes y orquestaciones son
`IOrchestrationNode`: se anidan, se exponen como herramienta (`AsTool`) y se ejecutan como pasos de un workflow.

```csharp
var team = Orchestrations.Parallel([legal, finanzas, mercado], aggregator: director);   // fan-out + síntesis
var pipeline = Orchestrations.Sequential(investigador, redactor, editor);               // cadena
var router = Orchestrations.Router(modeloBarato, soporte, ventas, team.Named("estrategia", "Preguntas de negocio"));
var jefe = Orchestrations.Supervisor(claude, investigador, analista, programador);      // planifica y delega (en paralelo)
var chat = Orchestrations.Handoffs(triage, ventas, facturacion);                        // se traspasan la conversación
var revisado = Orchestrations.EvaluatorOptimizer(redactor, evaluador, "Tono formal, sin errores, máx. 200 palabras");

var result = await router.RunAsync("¿Conviene abrir en Lima?", new OrchestrationOptions
{
    OnEvent = e => { Console.WriteLine(e); return ValueTask.CompletedTask; },   // inicio/fin de agentes, rutas, traspasos...
});
Console.WriteLine($"{result.Text} — {result.TotalUsage.TotalTokens} tokens, {result.Runs.Count} llamadas");

// Funciones como pasos, y orquestaciones como herramientas de otro agente
var limpiar = OrchestrationNodes.Function("limpiar", texto => texto.Trim());
var agente = new ToolLoopAgent(new() { Model = claude, Tools = [team.AsTool("comite", "Consulta al comité")] });

// Traspasos dentro de un chat existente (continúa con el agente que corresponda)
var turno = await chat.RunAsync(historial, startAgent: "triage");
```

### Artifacts (como Claude)

Los agentes crean contenido sustancial que se muestra aparte del chat (páginas HTML, componentes React,
documentos, código, diagramas Mermaid/SVG), lo editan con reemplazos exactos y guardan cada versión.

```csharp
var store = new FileSystemArtifactStore("data/artifacts");          // o InMemoryArtifactStore, o tu IArtifactStore
var agent = new ToolLoopAgent(new()
{
    Model = claude,
    Instructions = ArtifactTools.Instructions,                     // cuándo y cómo crear artifacts
    Tools = ArtifactTools.Create(store, new() { Url = a => $"/artifacts/{a.Id}" }),
});

app.MapArtifacts("/artifacts");   // GET /artifacts/{id}: página renderizada en un origen aislado (CSP sandbox)
```

Herramientas: `create_artifact`, `update_artifact` (reemplazo exacto), `rewrite_artifact`, `read_artifact`, `list_artifacts`.
Cada cambio llega a `useChat` como parte `data-artifact` (con id, tipo, versión y contenido) para mostrarlo en un panel;
`ArtifactRenderer.ToHtml(artifact)` convierte Markdown, Mermaid, React, SVG o CSV en una página autónoma.
Con `Context["conversationId"]` cada conversación solo ve y edita sus artifacts.

### Generar Word, PDF y Excel

```csharp
var style = new DocumentStyleOptions { Title = "Informe Q3", Author = "Ventas", TitlePage = true, HeaderText = "Confidencial" };
File.WriteAllBytes("informe.docx", DocumentGenerator.ToDocx(markdown, style));   // estilos reales de Word, editable
File.WriteAllBytes("informe.pdf", DocumentGenerator.ToPdf(markdown, style));     // sin navegador ni dependencias nativas

var xlsx = new SpreadsheetBuilder()
    .AddSheet("Ventas", ventas, new SheetOptions { Totals = true })               // desde objetos: tipos y formatos automáticos
    .AddSheet("Cálculo", [new("Item"), new("Precio", ColumnType.Currency), new("Cant."), new("Subtotal", ColumnType.Currency)],
              [["Mesa", 120.5, 2, "=B2*C2"]])                                    // fórmulas con "="
    .AddMarkdownTables(textoConTablas)                                            // una hoja por tabla Markdown
    .Build();
```

- **Word**: títulos, listas numeradas y con viñetas, tablas con encabezado, enlaces, citas, código, portada,
  encabezado y "Página n de N". Validado con el validador oficial de OpenXml.
- **PDF**: diagramación propia (ajuste de línea, tablas que se parten entre páginas repitiendo el encabezado, enlaces,
  código, citas, `\pagebreak`). Incluye las fuentes *AiSdk Sans/Mono* (derivadas de Liberation, licencia OFL) con
  acentos, ñ, griego y símbolos; para otros alfabetos: `PdfFonts.FromSingle(File.ReadAllBytes("NotoSansCJK.ttf"))`.
- **Excel**: columnas tipadas (moneda, porcentaje, fechas...), encabezado con estilo, filtros, panel fijo, anchos
  automáticos, fila de totales con `SUM` y fórmulas; entiende números como `1.234,50` y `12,5%`.

Para que el **agente** genere los archivos (se guardan como artifacts descargables):

```csharp
Tools = DocumentTools.Create(new DocumentToolOptions { Store = store, ArtifactOptions = new() { Url = a => $"/artifacts/{a.Id}" } }),
// create_word_document, create_pdf, create_spreadsheet  (+ DocumentTools.Instructions en el system prompt)
```

### Workflows durables, triggers y automatizaciones

`AiSdk.Workflows` ejecuta procesos largos que sobreviven reinicios (al estilo de Temporal, Inngest o el
Workflow DevKit de Vercel): cada paso se guarda y, al reanudar, los completados no se repiten.

```csharp
var refund = Workflow.Create<RefundRequest, string>("refund", async (ctx, r) =>
{
    var risk = await ctx.StepAsync("risk-check", ct => scoring.EvaluateAsync(r, ct));          // reintentos con backoff
    var resumen = await ctx.RunAgentAsync(analista, $"Resume el caso {r.OrderId}");             // agente como paso
    if (risk.High)
    {
        var ok = await ctx.WaitForEventAsync<Approval>("refund.approval", key: r.OrderId,        // aprobación humana
                                                       timeout: TimeSpan.FromDays(2));
        if (ok is not { Approved: true }) return "rechazado";
    }

    await ctx.SleepAsync("cooling-off", TimeSpan.FromHours(24));                               // duerme sin ocupar recursos
    await ctx.StepAsync("pay", ct => pagos.RefundAsync(r, ct), new StepOptions { MaxAttempts = 5 });
    return "reembolsado";
});

builder.Services.AddAiSdkWorkflows(w =>
{
    w.UseFileSystemStore("data/workflows");                     // o UseStore(tu IWorkflowStore: SQL, Redis...)
    w.AddWorkflow(refund);
    w.AddAutomation(Automation.Create("Informe diario", new CronTrigger("0 9 * * 1-5", "America/Santiago"), "daily-report", new { }));
    w.AddAutomation(new Automation { Id = "crm", Name = "Leads", Trigger = new WebhookTrigger(secret), Workflow = "lead" });
    w.AddAutomation(new Automation { Id = "paid", Name = "Pagados", Trigger = new EventTrigger("order.updated", new Dictionary<string, string> { ["status"] = "paid" }), Workflow = "invoice" });
});

app.MapWorkflows("/workflows").RequireAuthorization();
// POST /workflows/refund/start · GET /workflows/runs/{id} · POST /workflows/events/refund.approval?key=O-77
// POST /workflows/hooks/crm (X-Webhook-Secret o firma HMAC X-Signature-256) · /automations · /runs/{id}/retry
```

Disparadores: `CronTrigger` (5 o 6 campos, con zona horaria), `IntervalTrigger`, `OnceTrigger`, `EventTrigger`
(con filtro por contenido) y `WebhookTrigger` (secreto o firma HMAC-SHA256 en tiempo constante). Un servicio en segundo
plano dispara lo programado, reanuda ejecuciones dormidas y recupera las interrumpidas al arrancar.
`engine.RetryAsync(runId)` reintenta una ejecución fallida desde el paso que falló.

**Tareas programadas por agentes** ("revisa los precios cada día hábil a las 9"):

```csharp
w.ConfigureEngine((engine, sp) => engine.AddAgentTasks(new AgentTaskOptions
{
    Agent = sp => sp!.GetRequiredService<AsistenteAgent>(),
    OnCompleted = (r, sp, ct) => correo.EnviarAsync(r.Owner, r.Task, r.Text, ct),   // entrega del resultado
    DefaultTimeZone = "America/Santiago",
}));

Tools = AgentTasks.CreateTools(engine, options),   // schedule_task, list/cancel/run_scheduled_task_now, get_task_result
```

Cada usuario (`Context["userId"]`) solo ve y gestiona sus tareas, con un intervalo mínimo y un máximo de tareas configurables.

### Salida estructurada

```csharp
[Description("Una receta")]
public sealed record Recipe(string Name, List<string> Ingredients, int Minutes);

var recipe = (await Ai.GenerateObjectAsync<Recipe>(new() { Model = model, Prompt = "Empanadas de pino" })).Object;

// streaming de objetos parciales
await foreach (var partial in Ai.StreamObject<Recipe>(new() { Model = model, Prompt = "..." }).PartialObjectStream)
    Console.WriteLine(partial.Name);

// junto con herramientas, en un agente
var result = await Ai.GenerateTextAsync(new() { Model = model, Prompt = "...", Tools = [weather],
    Output = Output.Object<Weather>(), StopWhen = StopCondition.StepCountIs(5) });
var w = result.GetOutput<Weather>();

Output.Array<Recipe>();                 // listas
Output.Choice("positivo", "negativo");  // clasificación
```

El esquema se genera con `System.Text.Json.Schema.JsonSchemaExporter`, respetando `[Description]`,
nulabilidad, enums y parámetros de constructor requeridos. Los proveedores lo convierten al modo estricto
(`additionalProperties: false`) cuando lo exigen.

### Middleware y registro de proveedores

```csharp
var model = AnthropicProvider.Default.Messages("claude-opus-5-5")
    .WithMiddleware(
        Middlewares.DefaultSettings(maxOutputTokens: 4000),
        Middlewares.Logging(Console.WriteLine),
        new MiGuardrailMiddleware());   // implementa ILanguageModelMiddleware

var registry = new ProviderRegistry()
    .Register(AnthropicProvider.Default)
    .Register(OpenAIProvider.Default)
    .Alias("rapido", "anthropic:claude-haiku-5-5");

var m = registry.LanguageModel("openai:gpt-5");
```

### MCP (Model Context Protocol)

```csharp
using AiSdk.Mcp;

// stdio: lanza el servidor como proceso
await using var files = await McpClient.ConnectAsync(
    new StdioMcpTransport("npx", ["-y", "@modelcontextprotocol/server-filesystem", "/datos"]));

// Streamable HTTP (con cabeceras de autenticación); gestiona Mcp-Session-Id automáticamente
await using var remote = await McpClient.ConnectAsync(
    new HttpMcpTransport("https://mcp.ejemplo.com/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer ..." }));

List<Tool> tools = [.. await files.GetToolsAsync(), .. await remote.GetToolsAsync(prefix: "remoto_")];
var agent = new ToolLoopAgent(new AgentSettings { Model = model, Tools = tools, Instructions = files.Instructions });
```

Además de herramientas: `ListResourcesAsync`, `ReadResourceAsync` / `ReadResourceAsPartsAsync`,
`ListResourceTemplatesAsync`, `ListPromptsAsync` y `GetPromptAsync` (devuelve `ModelMessage` listos para usar).
Con `McpClientOptions` el cliente también atiende peticiones del servidor: `Roots`, `SamplingModel`
(el servidor genera texto con un modelo tuyo) y `ElicitationHandler` (pide datos al usuario).

`GetToolsAsync(include, prefix)` filtra herramientas y evita colisiones de nombre. Si una herramienta MCP
devuelve `isError`, el error se informa al modelo. También están `ListToolsAsync`, `CallToolAsync` y
`SendRequestAsync` para uso directo.

### Herramientas del proveedor y fuentes

Las ejecuta el proveedor en su servidor y los resultados llegan en la misma respuesta. El bucle no las
ejecuta localmente; las llamadas y resultados quedan en `result.Steps` con `ProviderExecuted = true`.

```csharp
// Claude
var result = await Ai.GenerateTextAsync(new GenerateTextOptions
{
    Model = anthropic.Messages("claude-opus-5-5"),
    Prompt = "¿Qué novedades trae .NET 10?",
    Tools = [AnthropicTools.WebSearch(maxUses: 3, allowedDomains: ["learn.microsoft.com"]), AnthropicTools.WebFetch()],
});

// OpenAI (API Responses)
var result2 = await Ai.GenerateTextAsync(new GenerateTextOptions
{
    Model = openai.Responses("gpt-5"),
    Prompt = "...",
    Tools = [OpenAITools.WebSearch(searchContextSize: "medium"), OpenAITools.CodeInterpreter()],
});

foreach (var source in result.Sources)
    Console.WriteLine($"{source.Title}: {source.Url}");
```

Disponibles: `AnthropicTools.WebSearch`, `WebFetch`, `CodeExecution`, `ServerTool(type, name)`;
`OpenAITools.WebSearch`, `CodeInterpreter`, `FileSearch`, `ServerTool(type, name)`. Con Claude, cuando la
API responde `pause_turn` (una herramienta de servidor necesita más tiempo), el proveedor continúa el turno
automáticamente. Las fuentes llegan al frontend como partes `source-url` del protocolo de `useChat`.

### Fallback entre proveedores

```csharp
var ollama = OpenAIProvider.Compatible("ollama", "http://localhost:11434/v1");
var model = anthropic.Messages("claude-opus-5-5")
    .WithFallback(openai.Responses("gpt-5"), ollama.Chat("llama3.2"));

// o en el registro / DI
registry.Fallback("chat", "anthropic:claude-opus-5-5", "openai:gpt-5");
services.AddAiSdk(ai => ai.AddAnthropic().AddOpenAI().AddFallback("chat", "anthropic:claude-opus-5-5", "openai:gpt-5"));
```

Cada error (salvo cancelaciones) pasa al siguiente modelo; con `new FallbackLanguageModel(models, shouldFallback, onFallback)`
decides qué errores cambian de modelo y recibes un aviso en cada cambio. En streaming solo cambia si el fallo
ocurre antes del primer fragmento.

### Reparación de llamadas a herramientas

```csharp
RepairToolCall = ToolCallRepair.FirstOf(
    ToolCallRepair.FixToolNameCase(),              // "Get-Weather" → "get_weather"
    ToolCallRepair.WithModel(modeloRapido)),       // pide a un modelo que corrija la entrada según el esquema
```

Se invoca cuando la herramienta no existe, el JSON está roto o la entrada no cumple el esquema
(`ToolCallRepairReason`). Si la reparación devuelve null, el error se informa al modelo como antes.

### Callbacks

```csharp
var stream = Ai.StreamText(new GenerateTextOptions
{
    Model = model,
    Prompt = "...",
    OnChunk = part => { /* texto, razonamiento, tools, fuentes */ return ValueTask.CompletedTask; },
    OnFinish = result => SaveUsageAsync(result.TotalUsage),
    OnError = ex => LogAsync(ex),
    OnAbort = steps => LogAsync($"Cancelado tras {steps.Count} pasos"),
    CancellationToken = http.RequestAborted,
});
```

Al cancelar, el stream termina con `AbortPart` (parte `abort` en el protocolo UI).

### Imágenes, voz y transcripción

```csharp
var image = await Ai.GenerateImageAsync(new GenerateImageOptions
{
    Model = openai.Image("gpt-image-1"), Prompt = "Un robot programando en C#", Size = "1024x1024", N = 2,
});
await image.Image.SaveAsync("robot.png");

var speech = await Ai.GenerateSpeechAsync(new GenerateSpeechOptions
{
    Model = openai.Speech("gpt-4o-mini-tts"), Text = "Hola desde .NET", Voice = "nova", Instructions = "Tono alegre",
});

var transcript = await Ai.TranscribeAsync(new TranscribeOptions
{
    Model = openai.Transcription("whisper-1"), Audio = await File.ReadAllBytesAsync("audio.mp3"), Language = "es",
});
Console.WriteLine(transcript.Text);   // y transcript.Segments con tiempos (whisper-1)
```

Las interfaces `IImageModel`, `ISpeechModel` e `ITranscriptionModel` permiten añadir otros proveedores;
el registro las resuelve con `registry.ImageModel("openai:gpt-image-1")`, etc.

### RAG: almacén vectorial y reranking

```csharp
var store = new InMemoryVectorStore(openai.Embedding("text-embedding-3-small"));   // o IVectorStore propio (pgvector, Qdrant...)
await store.AddTextsAsync([("manual", File.ReadAllText("manual.md"))], chunkSize: 1000, overlap: 100);

// RAG agéntico: el agente busca cuando lo necesita (con reranking opcional)
var agent = new ToolLoopAgent(new AgentSettings
{
    Model = model,
    Tools = [store.AsTool(topK: 5, reranker: CohereProvider.Default.Reranking())],
});

// RAG clásico: contexto en el prompt
var context = await store.BuildContextAsync("¿cómo instalo?");

// Reranking directo (Cohere, o EmbeddingRerankingModel con cualquier modelo de embeddings)
var ranked = await Ai.RerankAsync(CohereProvider.Default.Reranking(), "capital de Chile", documentos, topN: 3);
```

**Filtros por metadatos** (los traduce cada almacén a su lenguaje: SQL, filtros de Qdrant, OData…):

```csharp
var filtro = VectorFilter.Eq("tipo", "manual") & (VectorFilter.In("pais", "CL", "PE") | !VectorFilter.Exists("pais"));
var hits = await store.SearchAsync("garantía", topK: 5, filter: filtro);
var tool = store.AsTool(filter: VectorFilter.Eq("cliente", clienteId));   // multi-tenant
```

**PostgreSQL + pgvector** (`AiSdk.PgVector`): crea la tabla e índices (HNSW, GIN) automáticamente, y ofrece
búsqueda híbrida, que encuentra códigos, nombres y siglas que los embeddings pierden:

```csharp
await using var store = PgVectorStore.Create(connectionString, embeddings, new PgVectorStoreOptions
{
    Dimensions = 1536,
    Table = "documentos",
    TextSearchConfig = "spanish",
});
var hits = await store.HybridSearchAsync("error SKU-77812", topK: 5);
await store.DeleteWhereAsync(VectorFilter.Eq("source", "manual-viejo.pdf"));
var tool = store.AsTool(hybrid: true);
```

**Qdrant, Pinecone, Azure AI Search, Redis, Weaviate…** (`AiSdk.VectorData`): un adaptador sobre
Microsoft.Extensions.VectorData sirve para todos sus conectores. Los metadatos por los que se filtra se
declaran en `FilterableFields`:

```csharp
var qdrant = new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true);
var store = new VectorDataStore(qdrant, embeddings, new VectorDataStoreOptions
{
    CollectionName = "documentos",
    Dimensions = 1536,
    FilterableFields = ["tipo", "pais", "source"],
});
```

### Documentos a Markdown y OCR

`AiSdk.Documents` convierte archivos al formato que los modelos leen mejor y con menos tokens
(similar a MarkItDown o Docling). Las imágenes y páginas escaneadas pasan por OCR; los modelos con visión
describen gráficos y fotos; el audio y el video se transcriben.

| Formato | Resultado |
|---|---|
| PDF | Texto en orden de lectura, títulos por tamaño de fuente, sin encabezados/pies repetidos; páginas escaneadas con OCR |
| Word (.docx) | Títulos, listas numeradas y con viñetas, negrita/cursiva, enlaces, tablas, imágenes incrustadas (OCR/descripción) |
| Excel (.xlsx) | Una tabla por hoja, fechas y resultados de fórmulas, límite de filas configurable |
| PowerPoint (.pptx) | Una sección por diapositiva con título, viñetas, tablas, imágenes y notas del orador |
| HTML, CSV/TSV, JSON, XML, YAML, texto | Markdown limpio, tablas y bloques de código |
| Imágenes | Texto (OCR) y/o descripción con un modelo de visión |
| Audio / video | Transcripción con marcas de tiempo; en video, línea de tiempo con escenas (fotogramas con `ffmpeg`) |

```csharp
var converter = new MarkdownConverter(new MarkdownConverterOptions
{
    Ocr = new TesseractOcr("spa+eng"),            // local; o MistralProvider.Default.Ocr(), o new VisionModelOcr(claude)
    VisionModel = anthropic.Messages("claude-opus-5-5"),   // opcional: describe gráficos, fotos y fotogramas
    Transcription = openai.Transcription("gpt-4o-transcribe"),   // opcional: audio y video
    Language = "es",
});

var result = await converter.ConvertFileAsync("contrato-escaneado.pdf");
Console.WriteLine(result.Markdown);          // también: Pages, Title, Warnings, EstimatedTokens

// Directo a RAG: por página y fragmento, con metadatos source/page/title
await store.IngestAsync(converter, await DocumentSource.FromFileAsync("manual.docx"));
```

Motores OCR incluidos: `TesseractOcr` (sin enviar datos a terceros; `apt install tesseract-ocr tesseract-ocr-spa`),
`VisionModelOcr` (cualquier modelo con visión; lee tablas y escritura a mano) y `MistralOcr` (acepta PDF completos).
Para PDF escaneados se usa `pdftoppm` (poppler-utils) si está instalado o, si no, la imagen incrustada de cada página.

**Modelos sin visión ni soporte de archivos**: el middleware reemplaza imágenes, PDF, Office, audio y video por su
Markdown antes de llamar al modelo (con caché, para no reprocesar el historial en cada turno):

```csharp
var model = mistral.Chat("mistral-small-latest")
    .WithMiddleware(DocumentMiddlewares.ConvertAttachments(converter, new AttachmentConversionOptions
    {
        Native = NativeAttachments.None,              // o Images | Pdf si el modelo los soporta
        MaxTokensPerAttachment = 8000,
        CompressionEmbeddings = openai.Embedding("text-embedding-3-small"),   // conserva lo relevante a la pregunta
    }));
```

### Compresión de contexto

```csharp
// Conversaciones largas: recorta resultados de herramientas antiguos y resume los turnos viejos
// (incremental y cacheado) con un modelo barato; nunca separa una llamada a herramienta de su resultado.
var model = claude.WithMiddleware(Middlewares.CompressContext(
    maxInputTokens: 50_000, summarizer: anthropic.Messages("claude-haiku-5-5"), keepRecentMessages: 8));

// Textos largos
var relevante = await ContextCompressor.SelectRelevantAsync(embeddings, textoLargo, pregunta, maxTokens: 3000);
var resumen = await ContextCompressor.SummarizeAsync(modeloBarato, textoLargo, targetTokens: 2000, focus: "plazos y montos");
var tokens = TokenEstimator.Estimate(textoLargo);
```

### Conteo de tokens, batches y archivos

```csharp
// Tokens de entrada antes de enviar (Anthropic, OpenAI Responses, Gemini)
int tokens = await Ai.CountTokensAsync(new GenerateTextOptions { Model = model, Prompt = "...", Tools = [weather] });

// Lotes asíncronos a mitad de precio (Anthropic Message Batches u OpenAI Batch API)
IBatchClient batches = anthropic.Batches("claude-opus-5-5");     // o openai.Batches("gpt-5")
var job = await batches.CreateAsync([await options.ToBatchItemAsync("pedido-1"), ...]);
await batches.WaitForCompletionAsync(job.Id);
await foreach (var item in batches.GetResultsAsync(job.Id))
    Console.WriteLine($"{item.CustomId}: {item.Text}");

// Files API: sube una vez y referencia por id
var file = await anthropic.Files.UploadAsync(bytes, "contrato.pdf", "application/pdf");
var reply = await Ai.GenerateTextAsync(new() { Model = model, Messages = [ModelMessage.User(FilePart.FromFileId(file.Id), new TextPart("Resume"))] });

// Vector stores de OpenAI para la herramienta FileSearch
var vs = await openai.VectorStores.CreateAsync("docs");
await openai.VectorStores.UploadFileAsync(vs.Id, bytes, "guia.md", "text/markdown");
await openai.VectorStores.WaitUntilReadyAsync(vs.Id);
Tools = [OpenAITools.FileSearch([vs.Id])];
```

### Más proveedores

```csharp
var gemini  = GoogleProvider.Default.Gemini("gemini-2.5-flash");            // GEMINI_API_KEY
var mistral = MistralProvider.Default.Chat("mistral-large-latest");         // MISTRAL_API_KEY
var nova    = BedrockProvider.Default.Converse("amazon.nova-pro-v1:0");     // AWS_REGION + AWS_BEARER_TOKEN_BEDROCK o credenciales de AWS
var claudeBedrock = new BedrockProvider(o => { o.Region = "us-east-1"; o.CredentialsProvider = MisCredencialesAsync; })
    .Converse("us.anthropic.claude-sonnet-4-5-20250929-v1:0");

Tools = [GoogleTools.GoogleSearch(), GoogleTools.CodeExecution(), GoogleTools.UrlContext()];
ProviderOptions = GoogleOptions.Create(thinkingBudget: 2048, includeThoughts: true);

var image = await Ai.GenerateImageAsync(new() { Model = GoogleProvider.Default.Image("imagen-4.0-generate-001"), Prompt = "...", AspectRatio = "16:9" });
```

Bedrock firma las peticiones con AWS SigV4 (o usa la API key de Bedrock) y lee el formato binario
`event-stream` en streaming, sin depender del SDK de AWS.

### Streaming: suavizado y razonamiento en etiquetas

```csharp
var stream = Ai.StreamText(new GenerateTextOptions
{
    Model = deepseek.WithMiddleware(Middlewares.ExtractReasoning("think")),   // <think>...</think> → ReasoningPart
    Prompt = "...",
    StreamTransform = StreamTransforms.Smooth(TimeSpan.FromMilliseconds(15)), // palabra por palabra
});
```

`Middlewares.SimulateStreaming()` permite usar en streaming un modelo que solo implementa respuestas completas.

### ASP.NET Core + `useChat`

```csharp
builder.Services.AddAiSdk(ai => ai
    .AddAnthropic()
    .AddOpenAI()
    .AddAlias("chat", "anthropic:claude-opus-5-5")
    .AddDefaultModel("chat")
    .AddAgent<SupportAgent>());

app.MapPost("/api/chat", (UIChatRequest req, SupportAgent agent, HttpContext http) =>
    agent.Stream(new AgentCallOptions
    {
        Messages = UIMessageConverter.ToModelMessages(req.Messages),
        CancellationToken = http.RequestAborted,
    }).ToUIMessageStreamResult());
```

En el frontend: `useChat({ transport: new DefaultChatTransport({ api: "/api/chat" }) })`.

**Datos propios** (partes `data-*` en `useChat`): desde una herramienta con `ctx.WriteData("progress", new { percent = 50 }, id: "p")`,
o componiendo el stream:

```csharp
app.MapPost("/api/report", (UIChatRequest req, ILanguageModel model) =>
    UIMessageStreamResults.Create(writer =>
    {
        writer.WriteData("status", new { step = "buscando" }, id: "status");
        writer.Merge(Ai.StreamText(new() { Model = model, Messages = UIMessageConverter.ToModelMessages(req.Messages) }));
        return Task.CompletedTask;
    }));
```

**Streams reanudables**: la generación continúa aunque el usuario recargue la página y `useChat({ resume: true })` la retoma.

```csharp
builder.Services.AddAiSdk(ai => ai.AddAnthropic().AddResumableStreams());   // o tu IResumableStreamStore sobre Redis

app.MapPost("/api/chat", (UIChatRequest req, SupportAgent agent, IResumableStreamStore store) =>
    agent.Stream(UIMessageConverter.ToModelMessages(req.Messages)).ToResumableUIMessageStreamResult(store, req.Id!));

app.MapGet("/api/chat/{id}/stream", (string id, IResumableStreamStore store) => UIMessageStreamResults.Resume(store, id));
```

Ver `samples/ChatApi`.

### Microsoft.Extensions.AI

```csharp
using AiSdk.Extensions.AI;

IChatClient azure = /* AzureOpenAIClient, OllamaApiClient, ... */;
ILanguageModel model = azure.AsLanguageModel();   // ahora sirve para Ai.*, agentes y middleware
```

### Pruebas

```csharp
var model = new MockLanguageModel()
    .ReturnsToolCall("get_weather", new { city = "Lima" })
    .ReturnsText("En Lima hay 21°C");

var result = await agent.GenerateAsync("...");
Assert.Equal(2, model.Requests.Count);
```

Pruebas de integración contra las APIs reales en `tests/AiSdk.IntegrationTests`: se ejecutan con
`dotnet test` y cada proveedor se omite si su API key no está definida (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`,
`GEMINI_API_KEY`, `MISTRAL_API_KEY`, `COHERE_API_KEY`, Bedrock con `AWS_REGION` + `AISDK_TEST_BEDROCK_MODEL`).
La CI las ejecuta con los secretos del repositorio.

### Telemetría

Todas las llamadas emiten `Activity` en la fuente `"AiSdk"` (`ai.generateText`, `ai.streamText`,
`ai.generateText.step`, `ai.toolCall`):

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource(AiTelemetry.SourceName));
```

## Notas sobre proveedores

**Anthropic (Claude)**
- `AnthropicOptions.Create(effort, thinkingDisplay, cacheControl, betas, refusalFallback)` genera las opciones
  más usadas (`output_config.effort`, `thinking: {type: "adaptive"}`, caché automático, `fallbacks: "default"`).
- Los bloques de razonamiento se conservan con su firma (`ProviderMetadata["anthropic.signature"]`) y se
  reenvían sin cambios en los turnos siguientes.
- Los modelos Claude recientes rechazan `Temperature`/`TopP`/`TopK` y el `ToolChoice` forzado
  (`Required`/`Tool`); usa `ToolChoice.Auto` y controla el esfuerzo con `effort`.
- Con streaming se envía `eager_input_streaming: true` en las herramientas (configurable con
  `AnthropicProviderOptions.EagerInputStreaming`).

**OpenAI y compatibles**
- `openai.Responses("gpt-5")` usa la API Responses (recomendada): herramientas de servidor, resúmenes de
  razonamiento y citas. `openai.Chat(...)` usa Chat Completions, que es lo que implementan los servidores
  compatibles. `registry.LanguageModel("openai:...")` elige Responses para OpenAI y Chat para servidores compatibles.
- `OpenAIOptions.Create(reasoningEffort, reasoningSummary, store, parallelToolCalls, serviceTier, user)`;
  con `store: false` se pide el razonamiento cifrado para reenviarlo en los turnos siguientes.
- `ProviderOptions["openai"]` se fusiona en el cuerpo de la petición.
- `UseLegacyMaxTokens = true` para servidores que solo aceptan `max_tokens`.

**Google Gemini**
- Las firmas de razonamiento (`thoughtSignature`) se conservan y reenvían junto a las llamadas a funciones.
- La salida estructurada usa `responseJsonSchema`; las herramientas usan `parametersJsonSchema`.
- `GoogleOptions.Create(thinkingBudget, includeThoughts, thinkingLevel, safetySettings)`.

**Mistral**: `max_tokens`, `random_seed`, `tool_choice: "any"` y los bloques `thinking` de Magistral se manejan automáticamente.

**Amazon Bedrock**: Converse no tiene modo JSON universal; la salida estructurada se pide con una instrucción
de sistema y se valida al parsear. Las opciones propias del modelo van en
`ProviderOptions["bedrock"]["additionalModelRequestFields"]`.

Cualquier opción específica de un proveedor puede pasarse con `ProviderOptions`, que se fusiona
(en profundidad) con el cuerpo JSON de la petición.

## Estructura

```
src/
  AiSdk/                      núcleo
  AiSdk.OpenAI/               proveedor OpenAI + compatibles
  AiSdk.Anthropic/            proveedor Claude
  AiSdk.Google/               proveedor Gemini
  AiSdk.Mistral/              proveedor Mistral
  AiSdk.Bedrock/              proveedor Amazon Bedrock
  AiSdk.Cohere/               reranking y embeddings de Cohere
  AiSdk.Mcp/                  cliente MCP (stdio + HTTP)
  AiSdk.Extensions.AI/        adaptador Microsoft.Extensions.AI
  AiSdk.Extensions.DependencyInjection/
  AiSdk.AspNetCore/
  AiSdk.Documents/            documentos → Markdown, OCR, middleware de adjuntos; generación de Word/PDF/Excel
  AiSdk.Workflows/            workflows durables, triggers y automatizaciones
  AiSdk.PgVector/             almacén PostgreSQL + pgvector
  AiSdk.VectorData/           adaptador Microsoft.Extensions.VectorData
samples/
  QuickStart/                 ejemplos de consola
  ChatApi/                    backend para useChat
tests/AiSdk.Tests/            pruebas unitarias (modelos simulados + HTTP falso)
tests/AiSdk.IntegrationTests/ pruebas contra las APIs reales (opcionales)
```

## Pendiente / ideas

- Ejecutar las pruebas de integración con API keys reales (configurar los secretos en GitHub)
- Primera publicación en nuget.org (crear el tag `v0.1.0` con `NUGET_API_KEY` configurado)
- Azure OpenAI con autenticación Entra ID y Vertex AI nativos (hoy: endpoints compatibles o Microsoft.Extensions.AI)
- Almacén Redis para streams reanudables
- Detección de tablas en PDF digitales sin OCR (hoy: usar `PdfOcr = Always` con un OCR de modelo)
- Generative UI y herramientas de servidor adicionales (computer use, memoria)
- Almacenes `IWorkflowStore` para SQL Server/PostgreSQL/Redis y panel web de ejecuciones
- Workflows declarativos (JSON) para que los agentes armen flujos completos, no solo tareas
- Generación de PowerPoint y gráficos en Excel

## Licencia

MIT
