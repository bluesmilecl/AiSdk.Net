using System.ComponentModel;
using System.Text.Json;

namespace AiSdk.Tests;

public sealed record WeatherInput([property: Description("Ciudad")] string City);

public sealed record WeatherOutput(string City, int TemperatureC);

public class GenerateTextTests
{
    private static Tool WeatherTool(List<string>? calls = null) =>
        Tool.Create<WeatherInput, WeatherOutput>("get_weather", "Obtiene el clima", input =>
        {
            calls?.Add(input.City);
            return new WeatherOutput(input.City, 21);
        });

    [Fact]
    public async Task GeneratesText()
    {
        var model = new MockLanguageModel().ReturnsText("Hola mundo");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            System = "Eres útil",
            Prompt = "Saluda",
        });

        Assert.Equal("Hola mundo", result.Text);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Single(result.Steps);
        var sent = model.Requests[0].Messages;
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal("Saluda", sent[1].Text);
    }

    [Fact]
    public async Task RunsToolLoopUntilText()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Santiago" }, "call_1")
            .ReturnsText("En Santiago hay 21°C");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "¿Clima en Santiago?",
            Tools = [WeatherTool(calls)],
            StopWhen = StopCondition.StepCountIs(5),
        });

        Assert.Equal(["Santiago"], calls);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("En Santiago hay 21°C", result.Text);
        Assert.Equal(20, result.TotalUsage.InputTokens);

        // El segundo request incluye la llamada y su resultado.
        var second = model.Requests[1].Messages;
        var toolMessage = Assert.Single(second, m => m.Role == ChatRole.Tool);
        var toolResult = Assert.IsType<ToolResultPart>(Assert.Single(toolMessage.Content));
        Assert.Equal("call_1", toolResult.ToolCallId);
        Assert.Equal(21, toolResult.Output.GetProperty("temperatureC").GetInt32());

        // Herramientas enviadas con su esquema.
        var tool = Assert.Single(model.Requests[0].Tools!);
        Assert.Equal("Ciudad", tool.InputSchema.GetProperty("properties").GetProperty("city").GetProperty("description").GetString());

        // Mensajes de respuesta: assistant (tool call) + tool + assistant (texto).
        Assert.Equal([ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], result.ResponseMessages.Select(m => m.Role));
    }

    [Fact]
    public async Task DefaultStopConditionIsSingleStep()
    {
        var model = new MockLanguageModel().ReturnsToolCall("get_weather", new { city = "Lima" });

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", Tools = [WeatherTool()] });

        Assert.Single(result.Steps);
        Assert.Single(result.ToolResults);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task HasToolCallStopsLoop()
    {
        var done = Tool.Create<WeatherInput, string>("done", "Termina", _ => "ok");
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Lima" })
            .ReturnsToolCall("done", new { city = "-" })
            .ReturnsText("no debería llegar");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [WeatherTool(), done],
            StopWhen = StopCondition.StepCountIs(10) | StopCondition.HasToolCall("done"),
        });

        Assert.Equal(2, result.Steps.Count);
    }

    [Fact]
    public async Task ToolErrorsAreReturnedToModel()
    {
        var failing = Tool.Create<WeatherInput, string>("get_weather", "falla", (Func<WeatherInput, string>)(_ => throw new InvalidOperationException("servicio caído")));
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Lima" })
            .ReturnsToolCall("unknown_tool", new { })
            .ReturnsText("Lo siento");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [failing],
            StopWhen = StopCondition.StepCountIs(5),
        });

        Assert.Equal("Lo siento", result.Text);
        var first = Assert.Single(result.Steps[0].ToolResults);
        Assert.True(first.IsError);
        Assert.Equal("servicio caído", first.Output.GetString());
        var second = Assert.Single(result.Steps[1].ToolResults);
        Assert.True(second.IsError);
        Assert.Contains("Herramienta desconocida", second.Output.GetString());
    }

    [Fact]
    public async Task InvalidInputIsReportedAsError()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { wrong = 1 })
            .ReturnsText("ok");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [WeatherTool()],
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.True(result.Steps[0].ToolResults[0].IsError);
    }

    [Fact]
    public async Task ClientSideToolStopsLoopWithPendingCall()
    {
        var askUser = Tool.Define<WeatherInput>("ask_user", "Pregunta al usuario");
        var model = new MockLanguageModel().ReturnsToolCall("ask_user", new { city = "?" }, "call_x");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [askUser],
            StopWhen = StopCondition.StepCountIs(5),
        });

        Assert.Single(result.Steps);
        Assert.Equal("call_x", Assert.Single(result.PendingToolCalls).ToolCallId);
    }

    [Fact]
    public async Task ApprovalDeniedProducesErrorResult()
    {
        var executed = false;
        var tool = Tool.Create<WeatherInput, string>("delete_user", "Borra", _ => { executed = true; return "ok"; }).RequireApproval();
        var model = new MockLanguageModel()
            .ReturnsToolCall("delete_user", new { city = "x" })
            .ReturnsText("Cancelado");
        ToolApprovalRequest? asked = null;

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [tool],
            StopWhen = StopCondition.StepCountIs(5),
            OnToolApproval = req => { asked = req; return ValueTask.FromResult(false); },
        });

        Assert.False(executed);
        Assert.Equal("delete_user", asked!.ToolCall.ToolName);
        Assert.True(result.Steps[0].ToolResults[0].IsError);
        Assert.Equal("Cancelado", result.Text);
    }

    [Fact]
    public async Task ApprovalWithoutHandlerFailsClosed()
    {
        // Antes la llamada quedaba sin resultado (y el historial hacía fallar al proveedor): ahora falla cerrado.
        var executed = false;
        var tool = Tool.Create<WeatherInput, string>("delete_user", "Borra", _ => { executed = true; return "ok"; }).RequireApproval();
        var model = new MockLanguageModel().ReturnsToolCall("delete_user", new { city = "x" }).ReturnsText("No pude borrarlo.");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [tool],
            StopWhen = StopCondition.StepCountIs(5),
        });

        Assert.False(executed);
        Assert.Empty(result.PendingToolCalls);
        var error = Assert.Single(result.Steps[0].ToolResults);
        Assert.True(error.IsError);
        Assert.Contains("requiere aprobación", error.Output.GetString());
        Assert.Equal("No pude borrarlo.", result.Text);
    }

    [Fact]
    public async Task PrepareStepCanSwitchModelAndTools()
    {
        var first = new MockLanguageModel("first").ReturnsToolCall("get_weather", new { city = "Lima" });
        var second = new MockLanguageModel("second").ReturnsText("fin");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = first,
            Prompt = "x",
            Tools = [WeatherTool()],
            StopWhen = StopCondition.StepCountIs(5),
            PrepareStep = ctx => ValueTask.FromResult<PrepareStepResult?>(ctx.StepNumber == 0
                ? new PrepareStepResult { ToolChoice = ToolChoice.Required }
                : new PrepareStepResult { Model = second, ActiveTools = [] }),
        });

        Assert.Equal("fin", result.Text);
        Assert.Equal(ToolChoiceKind.Required, first.Requests[0].ToolChoice!.Kind);
        Assert.Null(second.Requests[0].Tools);
    }

    [Fact]
    public async Task OnStepFinishIsCalledPerStep()
    {
        var steps = new List<int>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Lima" })
            .ReturnsText("fin");

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [WeatherTool()],
            StopWhen = StopCondition.StepCountIs(5),
            OnStepFinish = s => { steps.Add(s.StepNumber); return ValueTask.CompletedTask; },
        });

        Assert.Equal([0, 1], steps);
    }

    [Fact]
    public async Task RetriesRetryableErrors()
    {
        var model = new MockLanguageModel()
            .ReturnsError(new ApiCallException("rate limit", 429, null, null, TimeSpan.FromMilliseconds(1)))
            .ReturnsText("ok");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x" });

        Assert.Equal("ok", result.Text);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task DoesNotRetryClientErrors()
    {
        var model = new MockLanguageModel().ReturnsError(new ApiCallException("bad", 400, null, null));

        var ex = await Assert.ThrowsAsync<ApiCallException>(() => Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x" }));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task ToolReceivesContextAndServices()
    {
        object? seen = null;
        var tool = Tool.Create<WeatherInput, string>("get_weather", "x", (input, ctx, ct) =>
        {
            seen = ctx.Context!["userId"];
            return Task.FromResult("ok");
        });
        var model = new MockLanguageModel().ReturnsToolCall("get_weather", new { city = "x" });

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [tool],
            Context = new Dictionary<string, object?> { ["userId"] = 42 },
        });

        Assert.Equal(42, seen);
    }

    [Fact]
    public void MessagesRoundTripThroughJson()
    {
        var messages = new List<ModelMessage>
        {
            ModelMessage.User(new TextPart("hola"), ImagePart.FromBytes([1, 2, 3])),
            ModelMessage.Assistant(new ReasoningPart("pienso"), new ToolCallPart("c1", "t", JsonElement.Parse("""{"a":1}"""))),
            ModelMessage.Tool(new ToolResultPart("c1", "t", JsonElement.Parse("\"ok\"")) { IsError = true }),
        };

        var json = JsonSerializer.Serialize(messages, JsonSchema.DefaultSerializerOptions);
        var back = JsonSerializer.Deserialize<List<ModelMessage>>(json, JsonSchema.DefaultSerializerOptions)!;

        Assert.Contains("\"type\":\"tool-call\"", json);
        Assert.Equal(3, back.Count);
        Assert.Equal([1, 2, 3], Assert.IsType<ImagePart>(back[0].Content[1]).Data);
        Assert.True(Assert.IsType<ToolResultPart>(back[2].Content[0]).IsError);
        Assert.Equal(1, Assert.IsType<ToolCallPart>(back[1].Content[1]).Input.GetProperty("a").GetInt32());
    }
}
