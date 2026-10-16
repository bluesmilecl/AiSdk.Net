// Ejemplos de AiSdk para .NET.
// Uso:  dotnet run --project samples/QuickStart -- [anthropic|openai|mock] [ejemplo]
// Ejemplos: text, stream, tools, agent, object, stream-object, session, subagents
//
// Variables de entorno: ANTHROPIC_API_KEY u OPENAI_API_KEY (el modo "mock" no necesita ninguna).

using System.ComponentModel;
using AiSdk;
using AiSdk.Anthropic;
using AiSdk.OpenAI;
using AiSdk.Testing;

var providerName = args.ElementAtOrDefault(0) ?? "anthropic";
var example = args.ElementAtOrDefault(1) ?? "agent";

ILanguageModel model = providerName switch
{
    "openai" => OpenAIProvider.Default.Chat("gpt-5"),
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
    _ => new MockLanguageModel().ReturnsText(".NET es una plataforma de desarrollo multiplataforma y de código abierto."),
};

public sealed record WeatherQuery([property: Description("Nombre de la ciudad")] string City);

public sealed record Weather(string City, int TemperatureC, string Conditions);

[Description("Una receta de cocina")]
public sealed record Recipe(
    [property: Description("Nombre de la receta")] string Name,
    [property: Description("Lista de ingredientes")] List<string> Ingredients,
    [property: Description("Tiempo de preparación en minutos")] int Minutes);
