// Ejemplos de AiSdk para .NET.
// Uso:  dotnet run --project samples/QuickStart -- [anthropic|openai|google|mistral|bedrock|mock] [ejemplo] [args...]
// Ejemplos: text, stream, tools, agent, object, stream-object, session, subagents,
//           web-search, fallback, repair, mcp, image, speech, transcribe, rag, count-tokens, batch,
//           document, attachments, compress, orchestrate, artifact, generate-docs, workflow, skills
//
//   document:    ... -- anthropic document informe.pdf   (PDF, Word, Excel, PowerPoint, imagen, audio, video → Markdown)
//   attachments: ... -- mistral attachments boleta.png   (modelo sin visión + OCR local con Tesseract)
//   mcp:        ... -- anthropic mcp npx -y @modelcontextprotocol/server-everything
//   image/speech/transcribe usan OpenAI (OPENAI_API_KEY); transcribe recibe la ruta de un audio.
//
// Variables de entorno: ANTHROPIC_API_KEY u OPENAI_API_KEY (el modo "mock" no necesita ninguna).

using System.ComponentModel;
using AiSdk;
using AiSdk.Anthropic;
using AiSdk.Bedrock;
using AiSdk.Artifacts;
using AiSdk.Documents;
using AiSdk.Documents.Generation;
using AiSdk.Google;
using AiSdk.Mcp;
using AiSdk.Mistral;
using AiSdk.OpenAI;
using AiSdk.Skills;
using AiSdk.Testing;
using AiSdk.Workflows;

var providerName = args.ElementAtOrDefault(0) ?? "anthropic";
var example = args.ElementAtOrDefault(1) ?? "agent";

ILanguageModel model = providerName switch
{
    "openai" => OpenAIProvider.Default.Responses("gpt-5"),
    "google" => GoogleProvider.Default.Gemini("gemini-2.5-flash"),
    "mistral" => MistralProvider.Default.Chat("mistral-large-latest"),
    "bedrock" => BedrockProvider.Default.Converse(Environment.GetEnvironmentVariable("BEDROCK_MODEL") ?? "amazon.nova-pro-v1:0"),
    "mock" => MockScript(example),
    _ => AnthropicProvider.Default.Messages("claude-opus-5-5"),
};

// Opciones recomendadas para Claude: esfuerzo explícito y fallback ante rechazos.
var providerOptions = providerName == "anthropic"
    ? AnthropicOptions.Create(effort: "medium", refusalFallback: true)
    : null;

// ---------------------------------------------------------------------------
// Herramientas
// ---------------------------------------------------------------------------
var weather = Tool.Create<WeatherQuery, Weather>(
    "get_weather",
    "Obtiene el clima actual de una ciudad",
    async (q, ct) =>
    {
        await Task.Delay(100, ct);
        return new Weather(q.City, Random.Shared.Next(5, 30), "soleado");
    });

var convert = Tool.FromDelegate(
    "celsius_to_fahrenheit",
    "Convierte grados Celsius a Fahrenheit",
    ([Description("Temperatura en °C")] double celsius) => celsius * 9 / 5 + 32);

switch (example)
{
    case "text":
    {
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            System = "Respondes en español, en una frase.",
            Prompt = "¿Qué es .NET?",
            ProviderOptions = providerOptions,
        });
        Console.WriteLine(result.Text);
        Console.WriteLine($"[tokens: {result.TotalUsage.InputTokens} in / {result.TotalUsage.OutputTokens} out]");
        break;
    }

    case "stream":
    {
        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Prompt = "Escribe un haiku sobre C#.",
            ProviderOptions = providerOptions,
        });
        await foreach (var chunk in stream.TextStream)
        {
            Console.Write(chunk);
        }

        Console.WriteLine();
        break;
    }

    case "tools":
    {
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "¿Qué temperatura hace en Santiago, en Fahrenheit?",
            Tools = [weather, convert],
            StopWhen = StopCondition.StepCountIs(5),
            ProviderOptions = providerOptions,
            OnStepFinish = step =>
            {
                foreach (var call in step.ToolCalls)
                {
                    Console.WriteLine($"  → {call.ToolName}({call.Input})");
                }

                return ValueTask.CompletedTask;
            },
        });
        Console.WriteLine(result.Text);
        break;
    }

    case "agent":
    {
        var agent = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Instructions = "Eres un asistente meteorológico. Usa las herramientas disponibles.",
            Tools = [weather, convert],
            ProviderOptions = providerOptions,
        });

        var stream = agent.Stream("Compara el clima de Santiago y Lima, en °F.");
        await foreach (var part in stream.FullStream)
        {
            switch (part)
            {
                case TextDeltaPart t:
                    Console.Write(t.Delta);
                    break;
                case ToolCallStreamPart c:
                    Console.WriteLine($"\n  → {c.ToolCall.ToolName}({c.ToolCall.Input})");
                    break;
                case ToolResultStreamPart r:
                    Console.WriteLine($"  ← {r.ToolResult.Output}");
                    break;
                case ErrorPart e:
                    Console.WriteLine($"\n[error] {e.Error.Message}");
                    break;
            }
        }

        Console.WriteLine($"\n[{(await stream.Steps).Count} pasos]");
        break;
    }

    case "object":
    {
        var result = await Ai.GenerateObjectAsync<Recipe>(new GenerateObjectOptions
        {
            Model = model,
            Prompt = "Genera una receta de empanadas de pino.",
            ProviderOptions = providerOptions,
        });
        Console.WriteLine($"{result.Object.Name} ({result.Object.Minutes} min)");
        foreach (var i in result.Object.Ingredients)
        {
            Console.WriteLine($" - {i}");
        }

        break;
    }

    case "stream-object":
    {
        var stream = Ai.StreamObject<Recipe>(new GenerateObjectOptions
        {
            Model = model,
            Prompt = "Genera una receta de sopaipillas.",
            ProviderOptions = providerOptions,
        });
        await foreach (var partial in stream.PartialObjectStream)
        {
            Console.WriteLine($"{partial.Name} — {partial.Ingredients?.Count ?? 0} ingredientes…");
        }

        break;
    }

    case "session":
    {
        var agent = new ToolLoopAgent(new AgentSettings { Model = model, Instructions = "Eres amable y breve.", ProviderOptions = providerOptions });
        var chat = agent.CreateSession();
        Console.WriteLine((await chat.SendAsync("Hola, me llamo Ana.")).Text);
        Console.WriteLine((await chat.SendAsync("¿Cómo me llamo?")).Text);
        break;
    }

    case "subagents":
    {
        var researcher = new ToolLoopAgent(new AgentSettings
        {
            Name = "weather_researcher",
            Description = "Investiga el clima de una o varias ciudades.",
            Model = model,
            Tools = [weather],
            ProviderOptions = providerOptions,
        });
        var orchestrator = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Instructions = "Delegas la investigación del clima al sub-agente y luego recomiendas qué ropa llevar.",
            Tools = [researcher.AsTool()],
            ProviderOptions = providerOptions,
        });
        Console.WriteLine((await orchestrator.GenerateAsync("Viajo a Lima mañana, ¿qué llevo?")).Text);
        break;
    }

    case "web-search":
    {
        // Herramienta ejecutada en el servidor del proveedor; las fuentes llegan en result.Sources.
        Tool search = providerName switch
        {
            "openai" => OpenAITools.WebSearch(),
            "google" => GoogleTools.GoogleSearch(),
            _ => AnthropicTools.WebSearch(maxUses: 3),
        };
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "¿Cuál es la última versión estable de .NET? Responde en una frase.",
            Tools = [search],
            ProviderOptions = providerOptions,
        });
        Console.WriteLine(result.Text);
        foreach (var source in result.Sources)
        {
            Console.WriteLine($"  [{source.Title}] {source.Url}");
        }

        break;
    }

    case "fallback":
    {
        // Si el modelo principal falla, se usa el siguiente.
        var resilient = (providerName == "mock"
                ? new MockLanguageModel("principal").ReturnsError(new ApiCallException("Servicio no disponible", 503, null, null))
                : model)
            .WithFallback(providerName == "mock" ? MockScript("text") : OpenAIProvider.Default.Responses("gpt-5-mini"));
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = resilient, Prompt = "¿Qué es .NET?", MaxRetries = 0 });
        Console.WriteLine(result.Text);
        break;
    }

    case "repair":
    {
        // Corrige nombres de herramienta mal escritos y, si no basta, pide a un modelo que repare la entrada.
        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "¿Qué temperatura hace en Santiago?",
            Tools = [weather],
            StopWhen = StopCondition.StepCountIs(5),
            RepairToolCall = ToolCallRepair.FirstOf(ToolCallRepair.FixToolNameCase(), ToolCallRepair.WithModel(model)),
            OnStepFinish = step =>
            {
                foreach (var call in step.ToolCalls)
                {
                    Console.WriteLine($"  → {call.ToolName}({call.Input})");
                }

                return ValueTask.CompletedTask;
            },
        });
        Console.WriteLine(result.Text);
        break;
    }

    case "mcp":
    {
        var command = args.ElementAtOrDefault(2) ?? "npx";
        string[] commandArgs = args.Length > 3 ? args[3..] : ["-y", "@modelcontextprotocol/server-everything"];
        await using var mcp = await McpClient.ConnectAsync(new StdioMcpTransport(command, commandArgs));
        var tools = await mcp.GetToolsAsync();
        Console.WriteLine($"Servidor MCP '{mcp.ServerInfo.Name}' con {tools.Count} herramientas: {string.Join(", ", tools.Select(t => t.Name))}");

        var agent = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Instructions = mcp.Instructions,
            Tools = tools,
            ProviderOptions = providerOptions,
        });
        Console.WriteLine((await agent.GenerateAsync("Usa una de tus herramientas y explica el resultado en una frase.")).Text);
        break;
    }

    case "image":
    {
        var result = await Ai.GenerateImageAsync(new GenerateImageOptions
        {
            Model = OpenAIProvider.Default.Image("gpt-image-1"),
            Prompt = "Un robot morado programando en C#, ilustración plana",
            Size = "1024x1024",
        });
        await result.Image.SaveAsync("robot.png");
        Console.WriteLine("Imagen guardada en robot.png");
        break;
    }

    case "speech":
    {
        var result = await Ai.GenerateSpeechAsync(new GenerateSpeechOptions
        {
            Model = OpenAIProvider.Default.Speech("gpt-4o-mini-tts"),
            Text = "Hola, este audio lo generó AiSdk para .NET.",
            Voice = "nova",
        });
        await result.Audio.SaveAsync("hola.mp3");
        Console.WriteLine("Audio guardado en hola.mp3");
        break;
    }

    case "transcribe":
    {
        var path = args.ElementAtOrDefault(2) ?? "hola.mp3";
        var result = await Ai.TranscribeAsync(new TranscribeOptions
        {
            Model = OpenAIProvider.Default.Transcription("gpt-4o-transcribe"),
            Audio = await File.ReadAllBytesAsync(path),
            MediaType = "audio/mpeg",
            Language = "es",
        });
        Console.WriteLine(result.Text);
        break;
    }

    case "rag":
    {
        // RAG agéntico: el agente decide cuándo buscar en la base de conocimiento.
        IEmbeddingModel embeddings = providerName switch
        {
            "openai" => OpenAIProvider.Default.Embedding("text-embedding-3-small"),
            "google" => GoogleProvider.Default.Embedding(),
            "mistral" => MistralProvider.Default.Embedding(),
            _ => new MockEmbeddingModel(dimensions: 64),
        };
        var store = new InMemoryVectorStore(embeddings);
        await store.AddTextsAsync(
        [
            ("politicas", "Las devoluciones se aceptan hasta 30 días después de la compra con boleta.\n\nEl envío es gratis sobre $30.000."),
            ("horarios", "La tienda abre de lunes a sábado de 10:00 a 20:00. Los domingos está cerrada."),
        ], chunkSize: 200);

        var agent = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Instructions = "Responde preguntas de clientes usando la base de conocimiento. Cita el documento.",
            Tools = [store.AsTool(topK: 2)],
            ProviderOptions = providerOptions,
        });
        Console.WriteLine((await agent.GenerateAsync("¿Hasta cuándo puedo devolver un producto?")).Text);
        break;
    }

    case "count-tokens":
    {
        var tokens = await Ai.CountTokensAsync(new GenerateTextOptions
        {
            Model = model,
            System = "Eres un asistente útil.",
            Prompt = "Explica qué es .NET en tres frases.",
            Tools = [weather],
        });
        Console.WriteLine($"Tokens de entrada: {tokens}");
        break;
    }

    case "batch":
    {
        // Lotes asíncronos a mitad de precio (Anthropic u OpenAI).
        IBatchClient batches = providerName == "openai"
            ? OpenAIProvider.Default.Batches("gpt-5")
            : AnthropicProvider.Default.Batches("claude-opus-5-5");
        var items = new List<BatchRequestItem>();
        foreach (var (id, question) in new[] { ("q1", "¿Qué es C#?"), ("q2", "¿Qué es F#?") })
        {
            items.Add(await new GenerateTextOptions { Model = model, Prompt = question, MaxOutputTokens = 300 }.ToBatchItemAsync(id));
        }

        var job = await batches.CreateAsync(items);
        Console.WriteLine($"Lote {job.Id} creado; esperando resultados...");
        await batches.WaitForCompletionAsync(job.Id, TimeSpan.FromSeconds(30), new Progress<BatchJob>(j => Console.WriteLine($"  {j.Status}: {j.Counts.Succeeded}/{j.Counts.Total}")));
        await foreach (var item in batches.GetResultsAsync(job.Id))
        {
            Console.WriteLine($"[{item.CustomId}] {(item.Succeeded ? item.Text : "error: " + item.Error)}");
        }

        break;
    }

    case "document":
    {
        // Documento → Markdown. OCR local con Tesseract si está instalado; el modelo describe imágenes.
        var path = args.ElementAtOrDefault(2) ?? throw new ArgumentException("Indique la ruta de un archivo.");
        var converter = new MarkdownConverter(new MarkdownConverterOptions
        {
            Ocr = new TesseractOcr() is { IsAvailable: true } tesseract ? tesseract : new VisionModelOcr(model),
            VisionModel = providerName == "mock" ? null : model,
            Transcription = Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } ? OpenAIProvider.Default.Transcription("gpt-4o-transcribe") : null,
            Language = "es",
        });
        var result = await converter.ConvertFileAsync(path);
        Console.WriteLine(result.Markdown);
        Console.WriteLine($"\n--- {result.Pages.Count} página(s), ~{result.EstimatedTokens} tokens{(result.Warnings.Count > 0 ? ", avisos: " + string.Join(" | ", result.Warnings) : string.Empty)}");

        // Indexar y preguntar sobre el documento.
        var store = new InMemoryVectorStore(new MockEmbeddingModel(dimensions: 64));
        await store.UpsertAsync(result.ToVectorDocuments(Path.GetFileName(path)));
        Console.WriteLine($"Indexado en {(await store.SearchAsync("resumen", topK: 100)).Count} fragmentos.");
        break;
    }

    case "attachments":
    {
        // Un modelo sin visión ni soporte de PDF recibe los adjuntos ya convertidos a Markdown.
        var path = args.ElementAtOrDefault(2);
        var bytes = path is null ? "Boleta: TOTAL $12.345"u8.ToArray() : await File.ReadAllBytesAsync(path);
        var textOnly = model.WithMiddleware(DocumentMiddlewares.ConvertAttachments(
            new MarkdownConverter(new MarkdownConverterOptions { Ocr = new TesseractOcr(), Language = "es" }),
            new AttachmentConversionOptions { MaxTokensPerAttachment = 4000 }));
        var answer = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = textOnly,
            Messages =
            [
                ModelMessage.User(
                    new TextPart("¿Cuál es el total de este documento?"),
                    new FilePart { Data = bytes, MediaType = "application/octet-stream", Filename = path is null ? "boleta.txt" : Path.GetFileName(path) }),
            ],
            ProviderOptions = providerOptions,
        });
        Console.WriteLine(answer.Text);
        break;
    }

    case "compress":
    {
        // Conversaciones largas: los turnos antiguos se resumen con un modelo económico.
        var summarizer = providerName switch
        {
            "anthropic" => AnthropicProvider.Default.Messages("claude-haiku-5-5"),
            "openai" => OpenAIProvider.Default.Responses("gpt-5-mini"),
            _ => model,
        };
        var compact = model.WithMiddleware(Middlewares.CompressContext(maxInputTokens: 2_000, summarizer, keepRecentMessages: 4));
        var history = new List<ModelMessage>();
        for (var i = 1; i <= 12; i++)
        {
            history.Add(ModelMessage.User($"Dato {i}: el cliente {i} compró {i * 3} unidades. " + new string('·', 800)));
            history.Add(ModelMessage.Assistant($"Anotado el dato {i}."));
        }

        history.Add(ModelMessage.User("¿Cuántas unidades compró el cliente 2?"));
        Console.WriteLine($"Historial: ~{TokenEstimator.Estimate(history)} tokens (límite 2.000)");
        Console.WriteLine((await Ai.GenerateTextAsync(new GenerateTextOptions { Model = compact, Messages = history, ProviderOptions = providerOptions })).Text);
        break;
    }

    case "orchestrate":
    {
        // Multi-agente: un router envía a especialistas; el de ventas usa un equipo en paralelo con un agregador.
        ToolLoopAgent Specialist(string name, string description, string instructions) =>
            new(new AgentSettings { Name = name, Description = description, Model = model, Instructions = instructions, ProviderOptions = providerOptions });

        var analysis = Orchestrations.Parallel(
            [
                Specialist("mercado", "Análisis de mercado", "Analiza el mercado y la competencia en 3 viñetas."),
                Specialist("finanzas", "Análisis financiero", "Estima costos e ingresos en 3 viñetas."),
            ],
            aggregator: Specialist("director", "Síntesis", "Integra los análisis en una recomendación de 5 líneas."));

        var router = Orchestrations.Router(
            model,
            Specialist("soporte", "Problemas técnicos con el producto", "Resuelve problemas técnicos paso a paso."),
            analysis.Named("estrategia", "Preguntas de negocio, precios o expansión"));

        var result = await router.RunAsync("¿Conviene abrir una sucursal en Lima?", new OrchestrationOptions
        {
            OnEvent = e =>
            {
                Console.WriteLine(e switch
                {
                    RoutedEvent r => $"→ ruta: {r.Route} ({r.Reason})",
                    NodeStartedEvent s => $"  ▶ {s.Node}",
                    NodeCompletedEvent c => $"  ✓ {c.Node} ({c.Usage.TotalTokens} tokens)",
                    _ => e.ToString(),
                });
                return ValueTask.CompletedTask;
            },
        });
        Console.WriteLine($"\n{result.Text}\n\nTotal: {result.TotalUsage.TotalTokens} tokens en {result.Runs.Count} llamadas");
        break;
    }

    case "artifact":
    {
        // El agente crea y edita artifacts (como Claude); aquí se guardan en ./artifacts.
        var store = new FileSystemArtifactStore("artifacts");
        var agent = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Instructions = ArtifactTools.Instructions,
            Tools = ArtifactTools.Create(store),
            ProviderOptions = providerOptions,
        });
        Console.WriteLine((await agent.GenerateAsync("Crea una calculadora de propinas en HTML y luego cambia el color principal a verde.")).Text);
        foreach (var artifact in await store.ListAsync())
        {
            var path = Path.Combine("artifacts", artifact.Id + ".html");
            await File.WriteAllTextAsync(path, ArtifactRenderer.ToHtml(artifact));
            Console.WriteLine($"{artifact.Title} (v{artifact.Version}) → {path}");
        }

        break;
    }

    case "generate-docs":
    {
        // Word, PDF y Excel sin modelo; con un agente, usa DocumentTools.Create(...).
        const string informe = """
            # Informe trimestral

            Las **ventas** crecieron un 12 % frente al trimestre anterior.

            | País | Ventas | Meta |
            |:-----|-------:|-----:|
            | Chile | 1.250 | 1.100 |
            | Perú | 980 | 1.000 |

            1. Reforzar inventario en Perú
            2. Ampliar canal digital
            """;
        var style = new DocumentStyleOptions { Title = "Informe trimestral", Author = "Equipo comercial", TitlePage = true, HeaderText = "Confidencial" };
        await File.WriteAllBytesAsync("informe.docx", DocumentGenerator.ToDocx(informe, style));
        await File.WriteAllBytesAsync("informe.pdf", DocumentGenerator.ToPdf(informe, style));
        await File.WriteAllBytesAsync("ventas.xlsx", new SpreadsheetBuilder()
            .AddSheet("Ventas", [new Sale("Café", DateTime.Today, 10, 1500.5m), new Sale("Té", DateTime.Today, 4, 980m)], new SheetOptions { Totals = true })
            .Build());
        Console.WriteLine("Generados: informe.docx, informe.pdf, ventas.xlsx");
        break;
    }

    case "workflow":
    {
        // Workflow durable con aprobación humana y un agente como paso.
        var writer = new ToolLoopAgent(new AgentSettings { Name = "redactor", Model = model, ProviderOptions = providerOptions });
        var engine = new WorkflowEngine(new FileSystemWorkflowStore("workflows")).Register(Workflow.Create<string, string>("campaign", async (ctx, product) =>
        {
            var draft = await ctx.RunAgentAsync(writer, $"Escribe un correo promocional de 3 líneas para {product}.", "draft");
            ctx.Log("Borrador listo, esperando aprobación");
            var approval = await ctx.WaitForEventAsync<Approval>("campaign.approval", key: product, timeout: TimeSpan.FromDays(1));
            if (approval is not { Approved: true })
            {
                return "Campaña cancelada";
            }

            await ctx.StepAsync("send", _ => Task.FromResult($"enviado a {approval.Audience}"));
            return $"Enviado:\n{draft}";
        }));

        var run = await engine.StartAsync("campaign", "café de especialidad");
        Console.WriteLine($"Estado: {run.Status} (esperando {run.Waiting?.EventName})");
        await engine.SendEventAsync("campaign.approval", new Approval(true, "clientes frecuentes"), key: "café de especialidad");
        await engine.WhenIdleAsync();
        Console.WriteLine((await engine.GetRunAsync(run.Id))!.GetOutput<string>());
        break;
    }

    case "skills":
    {
        // Skills (estándar Agent Skills): el agente ve solo el catálogo y carga cada skill cuando la necesita.
        var library = new SkillLibrary();
        library.Add(new SkillBuilder("cotizar-envios")
            .WithDescription("Calcula el costo de envíos nacionales. USE FOR: cotizar envíos, costo de despacho, tarifas por peso.")
            .WithInstructions("""
                # Cotizar envíos

                1. Pide peso (kg) y región de destino si faltan.
                2. Lee las tarifas en [references/tarifas.md](references/tarifas.md).
                3. Responde con el total y el desglose en una tabla.
                """)
            .AddFile("references/tarifas.md", "| Región | Base | Por kg |\n|---|---|---|\n| RM | $3.000 | $500 |\n| Sur | $5.000 | $800 |")
            .Build());
        if (Directory.Exists("skills"))
        {
            library.LoadDirectory("skills", new SkillLoadOptions { Trusted = true });   // las del proyecto (p. ej. skills/aisdk-net)
        }

        library.Activated += a => Console.WriteLine($"[skill activada: {a.Skill}]");
        var agent = new ToolLoopAgent(new AgentSettings { Model = model, Skills = library, ProviderOptions = providerOptions });
        Console.WriteLine((await agent.GenerateAsync("¿Cuánto cuesta enviar 4 kg al sur?")).Text);

        // El usuario también puede invocarlas como comando: /cotizar-envios 2 kg a la RM
        Console.WriteLine(library.TryInvokeSlashCommand("/cotizar-envios 2 kg a la RM")?[..60] + "…");
        break;
    }

    default:
        Console.WriteLine($"Ejemplo desconocido: {example}");
        break;
}

// Respuestas simuladas para ejecutar los ejemplos sin API key.
static MockLanguageModel MockScript(string example) => example switch
{
    "tools" or "agent" => new MockLanguageModel()
        .ReturnsToolCall("get_weather", new { city = "Santiago" })
        .ReturnsToolCall("celsius_to_fahrenheit", new { celsius = 20 })
        .ReturnsText("En Santiago hay 20°C (68°F)."),
    "subagents" => new MockLanguageModel()
        .ReturnsToolCall("weather_researcher", new { task = "Clima en Lima mañana" })
        .ReturnsToolCall("get_weather", new { city = "Lima" })
        .ReturnsText("Lima: 18°C, nublado.")
        .ReturnsText("Lleva una chaqueta ligera."),
    "object" or "stream-object" => new MockLanguageModel()
        .ReturnsText("""{"name":"Empanadas de pino","ingredients":["harina","carne","cebolla","huevo","aceitunas"],"minutes":90}"""),
    "session" => new MockLanguageModel().ReturnsText("¡Hola Ana!").ReturnsText("Te llamas Ana."),
    "attachments" => new MockLanguageModel().ReturnsText("El total es $12.345."),
    "orchestrate" => new MockLanguageModel()
        .ReturnsText("""{"route":"estrategia","reason":"pregunta de expansión"}""")
        .ReturnsText("- Mercado en crecimiento\n- Competencia media\n- Alta demanda digital")
        .ReturnsText("- Inversión: $200k\n- Retorno: 18 meses\n- Margen: 22%")
        .ReturnsText("Recomendación: abrir en Lima con foco digital; retorno estimado en 18 meses."),
    "artifact" => new MockLanguageModel()
        .ReturnsToolCall("create_artifact", new { title = "Calculadora de propinas", type = "html", content = "<!doctype html><title>Propinas</title><style>button{background:#2563eb;color:#fff}</style><input id=m type=number placeholder=Monto><button onclick=\"r.textContent=(m.value*0.1).toFixed(0)\">10%</button><p id=r></p>" })
        .ReturnsToolCall("update_artifact", new { id = "calculadora-de-propinas", oldStr = "#2563eb", newStr = "#16a34a" })
        .ReturnsText("Creé la calculadora y cambié el color a verde."),
    "skills" => new MockLanguageModel()
        .ReturnsToolCall("load_skill", new { name = "cotizar-envios" })
        .ReturnsToolCall("read_skill_file", new { skill = "cotizar-envios", path = "references/tarifas.md" })
        .ReturnsText("Envío de 4 kg al sur: $5.000 + 4 × $800 = $8.200."),
    "workflow" => new MockLanguageModel().ReturnsText("¡Nuevo café de especialidad! 20% de descuento esta semana."),
    "compress" => new MockLanguageModel().ReturnsText("- Cliente i compró 3·i unidades.").ReturnsText("El cliente 2 compró 6 unidades."),
    "web-search" => new MockLanguageModel().Returns(_ => new LanguageModelResult
    {
        Content =
        [
            new SourcePart("s1", "https://dotnet.microsoft.com") { Title = ".NET" },
            new TextPart("La última versión estable es .NET 10 (LTS)."),
        ],
        FinishReason = FinishReason.Stop,
    }),
    "repair" => new MockLanguageModel()
        .ReturnsToolCall("Get-Weather", new { city = "Santiago" })
        .ReturnsText("En Santiago hay 22°C."),
    "mcp" => new MockLanguageModel().ReturnsText("(modo mock: el servidor MCP se conectó, pero el modelo es simulado)"),
    "rag" => new MockLanguageModel()
        .ReturnsToolCall("search_knowledge_base", new { query = "plazo de devolución" })
        .ReturnsText("Puedes devolverlo hasta 30 días después de la compra, con boleta [politicas]."),
    _ => new MockLanguageModel().ReturnsText(".NET es una plataforma de desarrollo multiplataforma y de código abierto."),
};

public sealed record WeatherQuery([property: Description("Nombre de la ciudad")] string City);

public sealed record Weather(string City, int TemperatureC, string Conditions);

[Description("Una receta de cocina")]
public sealed record Recipe(
    [property: Description("Nombre de la receta")] string Name,
    [property: Description("Lista de ingredientes")] List<string> Ingredients,
    [property: Description("Tiempo de preparación en minutos")] int Minutes);

record Sale(string Producto, DateTime Fecha, int Unidades, decimal Monto);

record Approval(bool Approved, string? Audience = null);
