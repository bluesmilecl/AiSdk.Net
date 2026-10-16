# AiSdk para .NET

SDK de IA y agentes para **.NET 10**, inspirado en el [AI SDK de Vercel](https://ai-sdk.dev/docs/agents/overview)
y en el [AI SDK de Laravel](https://laravel.com/docs/ai-sdk). Una sola API, independiente del proveedor, para:

- generar texto y hacer streaming (`GenerateTextAsync`, `StreamText`)
- generar objetos tipados con JSON Schema a partir de tus clases (`GenerateObjectAsync<T>`, `StreamObject<T>`)
- herramientas (*tools*) con esquema automático y bucle de agente (`StopWhen`, `PrepareStep`, `OnStepFinish`)
- agentes (`ToolLoopAgent` o clases que heredan de `Agent`), memoria de conversación y sub-agentes
- aprobación humana de herramientas (*human-in-the-loop*)
- middleware de modelos, registro de proveedores con alias, reintentos y trazas OpenTelemetry
- embeddings y similitud coseno
- **cliente MCP** (stdio y HTTP): usa las herramientas de cualquier servidor MCP en tus agentes
- **herramientas del proveedor**: búsqueda web, web fetch, ejecución de código, búsqueda en archivos, con **fuentes/citas**
- **fallback entre proveedores**, **reparación** de llamadas a herramientas inválidas y callbacks `OnChunk`/`OnError`/`OnFinish`/`OnAbort`
- **imágenes, voz y transcripción** (`GenerateImageAsync`, `GenerateSpeechAsync`, `TranscribeAsync`)
- **RAG**: almacén vectorial, fragmentación de texto, reranking y búsqueda como herramienta del agente
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
```

Los paquetes se llaman `AiSdk.Net.*` (el id `AiSdk` ya existe en nuget.org); los namespaces son `AiSdk.*`.
Se publican con el workflow `release.yml` al crear un tag `vX.Y.Z` (requiere el secreto `NUGET_API_KEY`).

| Paquete | Contenido |
|---|---|
| `AiSdk` | Núcleo: API `Ai.*`, tools, agentes, `Output`, middleware, registro, embeddings, UI stream, `MockLanguageModel` |
| `AiSdk.OpenAI` | OpenAI: API Responses y Chat Completions, herramientas de servidor, embeddings, imágenes, voz y transcripción. `Chat` funciona con servidores compatibles (Ollama, Groq, DeepSeek, OpenRouter, LM Studio, vLLM…) |
| `AiSdk.Anthropic` | Claude (Messages API): tools, streaming, razonamiento con firma, salida estructurada, herramientas de servidor (web search, web fetch, code execution), citas, caché de prompts, fallback ante rechazos |
| `AiSdk.Google` | Gemini: tools, razonamiento con firmas, Google Search con fuentes, ejecución de código, URL context, embeddings, conteo de tokens e Imagen |
| `AiSdk.Mistral` | Mistral AI: chat, razonamiento de Magistral, tools, salida estructurada y embeddings |
| `AiSdk.Bedrock` | Amazon Bedrock (API Converse): Claude, Llama, Nova, Mistral… con SigV4 o API key; embeddings Titan y Cohere |
| `AiSdk.Cohere` | Reranking (Rerank) y embeddings (Embed) |
| `AiSdk.Mcp` | Cliente MCP (Model Context Protocol): herramientas, recursos, prompts, roots, sampling y elicitation; stdio y Streamable HTTP |
| `AiSdk.Extensions.AI` | Adaptador de `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) → cualquier proveedor de ese ecosistema |
| `AiSdk.Extensions.DependencyInjection` | `services.AddAiSdk(...)`, `IHttpClientFactory`, agentes en el contenedor |
| `AiSdk.AspNetCore` | `ToUIMessageStreamResult()` / `ToTextStreamResult()` para Minimal APIs y controladores |

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
`web-search`, `fallback`, `repair`, `mcp`, `image`, `speech`, `transcribe`.

```bash
# MCP con cualquier servidor (stdio):
dotnet run --project samples/QuickStart -- anthropic mcp npx -y @modelcontextprotocol/server-everything
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
- Almacenes vectoriales listos para pgvector/Qdrant y almacén Redis para streams reanudables
- Generative UI y herramientas de servidor adicionales (computer use, memoria)

## Licencia

MIT
