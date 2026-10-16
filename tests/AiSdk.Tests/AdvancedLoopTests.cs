using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.UI;

namespace AiSdk.Tests;

public class FallbackTests
{
    [Fact]
    public async Task FallsBackToNextModelOnError()
    {
        var primary = new MockLanguageModel("primary").ReturnsError(new ApiCallException("caído", 503, null, null));
        var backup = new MockLanguageModel("backup").ReturnsText("respuesta de respaldo");
        var switched = new List<string>();
        var model = new FallbackLanguageModel([primary, backup], onFallback: (m, _) => switched.Add(m.ModelId));

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", MaxRetries = 0 });

        Assert.Equal("respuesta de respaldo", result.Text);
        Assert.Equal(["primary"], switched);
    }

    [Fact]
    public async Task ThrowsLastErrorWhenAllModelsFail()
    {
        var model = new MockLanguageModel("a").ReturnsError(new ApiCallException("a", 500, null, null))
            .WithFallback(new MockLanguageModel("b").ReturnsError(new ApiCallException("b falla", 400, null, null)));

        var ex = await Assert.ThrowsAsync<ApiCallException>(() => Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", MaxRetries = 0 }));

        Assert.Equal("b falla", ex.Message);
    }

    [Fact]
    public async Task StreamFallsBackBeforeFirstChunk()
    {
        var model = new MockLanguageModel("a").ReturnsError(new HttpRequestException("sin red"))
            .WithFallback(new MockLanguageModel("b").ReturnsText("hola desde b"));

        var stream = Ai.StreamText(new GenerateTextOptions { Model = model, Prompt = "x", MaxRetries = 0 });

        Assert.Equal("hola desde b", await stream.Text);
    }

    [Fact]
    public async Task RegistryFallbackAlias()
    {
        var registry = new ProviderRegistry()
            .Register("p", new TestProvider(new Dictionary<string, MockLanguageModel>
            {
                ["down"] = new MockLanguageModel("down").ReturnsError(new ApiCallException("x", 500, null, null)),
                ["up"] = new MockLanguageModel("up").ReturnsText("ok"),
            }))
            .Fallback("chat", "p:down", "p:up");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = registry.LanguageModel("chat"), Prompt = "x", MaxRetries = 0 });

        Assert.Equal("ok", result.Text);
    }

    private sealed class TestProvider(Dictionary<string, MockLanguageModel> models) : IProvider
    {
        public string Name => "p";

        public ILanguageModel LanguageModel(string modelId) => models[modelId];
    }
}

public class CallbackTests
{
    [Fact]
    public async Task GenerateInvokesOnFinishAndOnError()
    {
        GenerateTextResult? finished = null;
        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsText("ok"),
            Prompt = "x",
            OnFinish = r => { finished = r; return ValueTask.CompletedTask; },
        });
        Assert.Equal("ok", finished!.Text);

        Exception? error = null;
        await Assert.ThrowsAsync<ApiCallException>(() => Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsError(new ApiCallException("bad", 400, null, null)),
            Prompt = "x",
            OnError = e => { error = e; return ValueTask.CompletedTask; },
        }));
        Assert.IsType<ApiCallException>(error);
    }

    [Fact]
    public async Task StreamInvokesOnChunkOnFinishAndOnError()
    {
        var chunks = new List<StreamPart>();
        GenerateTextResult? finished = null;
        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = new MockLanguageModel { StreamChunkSize = 2 }.ReturnsText("abcdef"),
            Prompt = "x",
            OnChunk = p => { chunks.Add(p); return ValueTask.CompletedTask; },
            OnFinish = r => { finished = r; return ValueTask.CompletedTask; },
        });
        await stream.ConsumeAsync();

        Assert.Equal(3, chunks.OfType<TextDeltaPart>().Count());
        Assert.DoesNotContain(chunks, c => c is StartStepPart or FinishPart);
        Assert.Equal("abcdef", finished!.Text);

        Exception? error = null;
        var failing = Ai.StreamText(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsError(new ApiCallException("bad", 400, null, null)),
            Prompt = "x",
            OnError = e => { error = e; return ValueTask.CompletedTask; },
        });
        await failing.ConsumeAsync();
        Assert.IsType<ApiCallException>(error);
    }

    [Fact]
    public async Task CancellationInvokesOnAbortAndEmitsAbortPart()
    {
        using var cts = new CancellationTokenSource();
        IReadOnlyList<StepResult>? aborted = null;
        var tool = Tool.Create<WeatherInput, string>("slow", "lenta", async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return "nunca";
        });

        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsToolCall("slow", new { city = "x" }),
            Prompt = "x",
            Tools = [tool],
            CancellationToken = cts.Token,
            OnAbort = steps => { aborted = steps; return ValueTask.CompletedTask; },
        });

        var parts = new List<StreamPart>();
        await foreach (var p in stream.FullStream)
        {
            parts.Add(p);
        }

        Assert.IsType<AbortPart>(parts[^1]);
        Assert.NotNull(aborted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.Text);
    }
}

public class ToolRepairTests
{
    private static Tool Weather(List<string>? calls = null) =>
        Tool.Create<WeatherInput, string>("get_weather", "clima", i => { calls?.Add(i.City); return $"21° en {i.City}"; });

    [Fact]
    public async Task FixesMisspelledToolName()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("Get-Weather", new { city = "Lima" }, "c1")
            .ReturnsText("listo");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Weather(calls)],
            StopWhen = StopCondition.StepCountIs(3),
            RepairToolCall = ToolCallRepair.FixToolNameCase(),
        });

        Assert.Equal(["Lima"], calls);
        Assert.Equal("get_weather", result.Steps[0].ToolCalls[0].ToolName); // la llamada reparada reemplaza a la original
        Assert.False(result.Steps[0].ToolResults[0].IsError);
    }

    [Fact]
    public async Task RepairsInvalidInputWithAModel()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { ciudad = "Quito" }, "c1")
            .ReturnsText("listo");
        var repairModel = new MockLanguageModel("repair").ReturnsText("""{"city":"Quito"}""");
        ToolCallRepairContext? context = null;

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Weather(calls)],
            StopWhen = StopCondition.StepCountIs(3),
            RepairToolCall = async ctx =>
            {
                context = ctx;
                return await ToolCallRepair.WithModel(repairModel)(ctx);
            },
        });

        Assert.Equal(["Quito"], calls);
        Assert.Equal(ToolCallRepairReason.InvalidInput, context!.Reason);
        Assert.Equal("c1", result.Steps[0].ToolResults[0].ToolCallId);
        Assert.Equal(ResponseFormatType.Json, repairModel.Requests[0].ResponseFormat!.Type);
        Assert.Contains("ciudad", repairModel.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task RepairsInvalidJson()
    {
        var model = new MockLanguageModel()
            .Returns(_ => new LanguageModelResult
            {
                Content = [new ToolCallPart("c1", "get_weather", JsonSerializer.SerializeToElement("{\"city\": \"Li")) { InvalidInput = true }],
                FinishReason = FinishReason.ToolCalls,
            })
            .ReturnsText("ok");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Weather()],
            StopWhen = StopCondition.StepCountIs(3),
            RepairToolCall = ctx => ValueTask.FromResult<ToolCallPart?>(
                ctx.Reason == ToolCallRepairReason.InvalidJson
                    ? ctx.ToolCall with { Input = JsonDocument.Parse("""{"city":"Lima"}""").RootElement, InvalidInput = false }
                    : null),
        });

        Assert.Equal("21° en Lima", result.Steps[0].ToolResults[0].Output.GetString());
        Assert.Contains("°", result.Steps[0].ToolResults[0].Output.GetRawText()); // sin escapar como \u00B0
    }

    [Fact]
    public async Task UnrepairableCallsReturnErrorToModel()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { wrong = 1 })
            .ReturnsText("ok");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Weather()],
            StopWhen = StopCondition.StepCountIs(3),
            RepairToolCall = _ => ValueTask.FromResult<ToolCallPart?>(null),
        });

        Assert.True(result.Steps[0].ToolResults[0].IsError);
        Assert.StartsWith("Entrada inválida", result.Steps[0].ToolResults[0].Output.GetString());
    }
}

public class ProviderToolLoopTests
{
    private static LanguageModelResult ServerSearchResponse(string text) => new()
    {
        Content =
        [
            new ToolCallPart("srv_1", "web_search", JsonDocument.Parse("""{"query":"clima"}""").RootElement) { ProviderExecuted = true },
            new ToolResultPart("srv_1", "web_search", JsonDocument.Parse("""[{"url":"https://a.cl"}]""").RootElement) { ProviderExecuted = true },
            new SourcePart("s1", "https://a.cl") { Title = "A" },
            new TextPart(text),
        ],
        FinishReason = FinishReason.Stop,
    };

    [Fact]
    public async Task ProviderExecutedToolsAreNotRunLocallyNorPending()
    {
        var model = new MockLanguageModel().Returns(_ => ServerSearchResponse("Según a.cl, 20°"));
        var search = Tool.ProviderDefined("mock", "web_search", new JsonObject { ["type"] = "web_search" });

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [search],
            StopWhen = StopCondition.StepCountIs(5),
        });

        Assert.Single(result.Steps);
        Assert.Empty(result.PendingToolCalls);
        Assert.Equal("https://a.cl", Assert.Single(result.Sources).Url);
        Assert.True(model.Requests[0].Tools![0].IsProviderTool);
        // Llamada y resultado del proveedor quedan en el mensaje del asistente (para reenviarlos).
        var assistant = Assert.Single(result.ResponseMessages);
        Assert.Equal(ChatRole.Assistant, assistant.Role);
        Assert.Contains(assistant.Content, p => p is ToolResultPart { ProviderExecuted: true });
    }

    [Fact]
    public async Task StreamEmitsSourcesAndProviderToolPartsToUI()
    {
        var model = new MockLanguageModel().Returns(_ => ServerSearchResponse("hola"));
        var stream = Ai.StreamText(new GenerateTextOptions { Model = model, Prompt = "x" });

        using var output = new MemoryStream();
        await UIMessageStream.WriteAsync(stream.FullStream, output);
        var text = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("\"type\":\"source-url\"", text);
        Assert.Contains("\"providerExecuted\":true", text);
        Assert.Single((await stream.Result).Sources);
    }

    [Fact]
    public void UIMessagesKeepProviderToolResultsInAssistantMessage()
    {
        var messages = UIMessageConverter.ToModelMessages(
        [
            new UIMessage
            {
                Role = "assistant",
                Parts =
                [
                    JsonNode.Parse("""{"type":"tool-web_search","toolCallId":"s1","state":"output-available","input":{},"output":[],"providerExecuted":true}""")!.AsObject(),
                    JsonNode.Parse("""{"type":"source-url","sourceId":"x","url":"https://a.cl"}""")!.AsObject(),
                    JsonNode.Parse("""{"type":"text","text":"hola"}""")!.AsObject(),
                ],
            },
        ]);

        var assistant = Assert.Single(messages);
        Assert.True(Assert.IsType<ToolCallPart>(assistant.Content[0]).ProviderExecuted);
        Assert.True(Assert.IsType<ToolResultPart>(assistant.Content[1]).ProviderExecuted);
        Assert.IsType<SourcePart>(assistant.Content[2]);
    }
}
