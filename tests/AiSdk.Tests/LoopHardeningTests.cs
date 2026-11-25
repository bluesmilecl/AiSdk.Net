using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiSdk.Internal;

namespace AiSdk.Tests;

/// <summary>Regresiones del bucle de herramientas: aprobaciones, paralelismo, reparación, JSON parcial y alias.</summary>
[Collection(nameof(PerformanceSensitive))]
public sealed class LoopHardeningTests
{
    private sealed record Input(string City);

    private static Tool Dangerous(Action? onExecute = null) =>
        Tool.Create<Input, string>("delete_user", "Borra un usuario", input => { onExecute?.Invoke(); return $"borrado {input.City}"; }).RequireApproval();

    private static Tool Weather() => Tool.Create<Input, string>("get_weather", "Clima", input => $"Soleado en {input.City}");

    private static LanguageModelResult Calls(params (string Id, string Tool)[] calls) => new()
    {
        Content = calls.Select(c => (ContentPart)new ToolCallPart(c.Id, c.Tool, JsonSerializer.SerializeToElement(new { city = "x" }))).ToList(),
        FinishReason = FinishReason.ToolCalls,
        Usage = new Usage { InputTokens = 1, OutputTokens = 1 },
    };

    /// <summary>True si cada llamada a herramienta del historial tiene su resultado (lo que exigen los proveedores).</summary>
    private static bool IsValidForProviders(IReadOnlyList<ModelMessage> messages)
    {
        var results = messages.SelectMany(m => m.Content.OfType<ToolResultPart>()).Select(r => r.ToolCallId).ToHashSet();
        return messages.SelectMany(m => m.Content.OfType<ToolCallPart>()).All(c => results.Contains(c.ToolCallId));
    }

    // -----------------------------------------------------------------
    // #6 Aprobaciones
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeferredApprovalsStopTheLoopAndResumeLater()
    {
        var executed = 0;
        var model = new MockLanguageModel()
            .Returns(_ => Calls(("c1", "delete_user"), ("c2", "get_weather")))
            .ReturnsText("Usuario borrado.");
        var tools = new[] { Dangerous(() => executed++), Weather() };

        var first = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "Borra a x y dime el clima",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(5),
            DeferToolApprovals = true,
        });

        Assert.Single(first.Steps);
        Assert.Equal("c1", Assert.Single(first.PendingApprovals).ToolCall.ToolCallId);
        Assert.Equal("get_weather", Assert.Single(first.ToolResults).ToolName); // la que no requiere aprobación se ejecutó
        Assert.Equal(0, executed);

        // Más tarde (p. ej. otra petición HTTP), el host aprueba y reanuda.
        var history = new List<ModelMessage> { ModelMessage.User("Borra a x y dime el clima") };
        history.AddRange(first.ResponseMessages);
        var resumed = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Messages = history,
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(5),
            DeferToolApprovals = true,
            ToolApprovals = [new ToolApprovalResponse("c1", Approved: true)],
        });

        Assert.Equal(1, executed);
        Assert.Equal("Usuario borrado.", resumed.Text);
        var resolved = resumed.Steps[0].ResponseMessages[0];
        Assert.Equal(ChatRole.Tool, resolved.Role);
        Assert.Equal("borrado x", Assert.Single(resolved.Content.OfType<ToolResultPart>()).Output.GetString());
        Assert.True(IsValidForProviders(model.Requests[1].Messages));
        Assert.True(IsValidForProviders([.. history, .. resumed.ResponseMessages]));
    }

    [Fact]
    public async Task DeniedOrUnansweredApprovalsAreReportedToTheModelWhenResuming()
    {
        var executed = 0;
        var model = new MockLanguageModel()
            .Returns(_ => Calls(("c1", "delete_user"), ("c2", "delete_user")))
            .ReturnsText("Entendido.");
        var tools = new[] { Dangerous(() => executed++) };
        var first = await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", Tools = tools, DeferToolApprovals = true });

        var resumed = Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Messages = [ModelMessage.User("x"), .. first.ResponseMessages],
            Tools = tools,
            ToolApprovals = [new ToolApprovalResponse("c1", Approved: false, Reason: "no está autorizado")],
        });
        var parts = new List<StreamPart>();
        await foreach (var part in resumed.FullStream)
        {
            parts.Add(part);
        }

        Assert.Equal(0, executed);
        // La denegada llega como ToolOutputDeniedStreamPart (tool-output-denied en useChat); la que no tuvo decisión, como error.
        var denied = Assert.Single(parts.OfType<ToolOutputDeniedStreamPart>()).ToolResult;
        var unanswered = Assert.Single(parts.OfType<ToolResultStreamPart>()).ToolResult;
        Assert.True(denied.IsError && unanswered.IsError);
        Assert.Equal(("c1", "c2"), (denied.ToolCallId, unanswered.ToolCallId));
        Assert.Contains("no está autorizado", denied.Output.GetString());
        Assert.Contains("no hubo una decisión", unanswered.Output.GetString());
        Assert.Equal("Entendido.", await resumed.Text);
    }

    [Fact]
    public async Task SessionsNeverKeepDanglingToolCalls()
    {
        var store = new InMemoryConversationStore();
        var model = new MockLanguageModel()
            .Returns(_ => Calls(("c1", "delete_user")))
            .ReturnsText("De acuerdo, otra cosa.")
            .Returns(_ => Calls(("c2", "delete_user")))
            .ReturnsText("Hecho.");
        var executed = 0;
        var agent = new ToolLoopAgent(new AgentSettings { Model = model, Tools = [Dangerous(() => executed++)], DeferToolApprovals = true });
        var session = agent.CreateSession("s1", store);

        // 1) Queda pendiente; 2) el usuario cambia de tema sin responder: la llamada se cierra (y se guarda el cierre).
        var pending = await session.SendAsync("Borra a x", TestContext.Current.CancellationToken);
        Assert.Single(pending.PendingApprovals);
        Assert.Equal("c1", Assert.Single(await session.GetPendingToolCallsAsync(TestContext.Current.CancellationToken)).ToolCallId);
        await session.SendAsync("Olvídalo", TestContext.Current.CancellationToken);
        Assert.True(IsValidForProviders(model.Requests[1].Messages));
        Assert.True(IsValidForProviders(await session.GetMessagesAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(await session.GetPendingToolCallsAsync(TestContext.Current.CancellationToken));

        // 3) Otra pendiente que esta vez se aprueba y se reanuda desde la sesión.
        await session.SendAsync("Borra a y", TestContext.Current.CancellationToken);
        var done = await session.SendApprovalsAsync([new ToolApprovalResponse("c2", Approved: true)], TestContext.Current.CancellationToken);
        Assert.Equal("Hecho.", done.Text);
        Assert.Equal(1, executed);
        Assert.True(IsValidForProviders(await session.GetMessagesAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task SessionsRepairHistoriesCorruptedByEarlierVersions()
    {
        // Así quedaba el historial con 0.1.0: una llamada sin resultado hacía fallar todas las peticiones siguientes.
        var store = new InMemoryConversationStore();
        await store.AppendAsync("s1", [
            ModelMessage.User("Borra a x"),
            ModelMessage.Assistant(new ToolCallPart("old", "delete_user", JsonSerializer.SerializeToElement(new { city = "x" }))),
            ModelMessage.User("¿Y bien?"),
            ModelMessage.Assistant("No pude."),
        ], TestContext.Current.CancellationToken);
        var model = new MockLanguageModel().ReturnsText("Hola");
        var session = new ToolLoopAgent(new AgentSettings { Model = model, Tools = [Dangerous()] }).CreateSession("s1", store);

        await session.SendAsync("Hola", TestContext.Current.CancellationToken);

        var sent = model.Requests[0].Messages;
        Assert.True(IsValidForProviders(sent));
        var closure = sent[sent.ToList().FindIndex(m => m.Content.OfType<ToolCallPart>().Any()) + 1];
        Assert.True(Assert.Single(closure.Content.OfType<ToolResultPart>()).IsError);
    }

    [Fact]
    public async Task ApprovalWithoutHandlerInASessionKeepsTheHistoryValid()
    {
        var model = new MockLanguageModel().ReturnsToolCall("delete_user", new { city = "x" }, "c1").ReturnsText("No pude.").ReturnsText("Hola");
        var session = new ToolLoopAgent(new AgentSettings { Model = model, Tools = [Dangerous()] }).CreateSession();

        await session.SendAsync("Borra a x", TestContext.Current.CancellationToken);
        await session.SendAsync("Hola", TestContext.Current.CancellationToken);

        Assert.True(IsValidForProviders(model.Requests[2].Messages));
    }

    [Fact]
    public async Task SubAgentsAskTheParentApprovalHandlerOrFailExplicitly()
    {
        ToolLoopAgent SubAgent(MockLanguageModel model, bool defer = false) => new(new AgentSettings
        {
            Name = "Operador",
            Model = model,
            Tools = [Dangerous()],
            DeferToolApprovals = defer,
        });

        // El manejador del agente principal decide sobre las herramientas del sub-agente.
        ToolApprovalRequest? asked = null;
        var sub = new MockLanguageModel().ReturnsToolCall("delete_user", new { city = "x" }).ReturnsText("Borrado.");
        var parent = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsToolCall("operador", new { task = "borra a x" }).ReturnsText("Listo."),
            Prompt = "x",
            Tools = [SubAgent(sub).AsTool()],
            StopWhen = StopCondition.StepCountIs(3),
            OnToolApproval = request => { asked = request; return ValueTask.FromResult(true); },
        });
        Assert.Equal("delete_user", asked!.ToolCall.ToolName);
        Assert.Equal("Borrado.", parent.Steps[0].ToolResults[0].Output.GetString());

        // Sin manejador en ningún nivel: el sub-agente recibe un error explícito (no se ejecuta en silencio).
        var unattended = new MockLanguageModel().ReturnsToolCall("delete_user", new { city = "x" }).ReturnsText("Necesito aprobación.");
        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsToolCall("operador", new { task = "borra a x" }).ReturnsText("x"),
            Prompt = "x",
            Tools = [SubAgent(unattended).AsTool()],
            StopWhen = StopCondition.StepCountIs(3),
        });
        Assert.Contains("requiere aprobación", unattended.Requests[1].Messages[^1].Content.OfType<ToolResultPart>().Single().Output.GetString());

        // Un sub-agente que difiere aprobaciones no puede reanudarse: error explícito en vez de texto vacío.
        var deferred = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel().ReturnsToolCall("operador", new { task = "borra a x" }).ReturnsText("x"),
            Prompt = "x",
            Tools = [SubAgent(new MockLanguageModel().ReturnsToolCall("delete_user", new { city = "x" }), defer: true).AsTool()],
            StopWhen = StopCondition.StepCountIs(3),
        });
        var error = deferred.Steps[0].ToolResults[0];
        Assert.True(error.IsError);
        Assert.Contains("sin resolver", error.Output.GetString());
    }

    // -----------------------------------------------------------------
    // #8 Paralelismo y cancelación de la reparación
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ToolCallsInOneStepRespectMaxParallelism(int limit)
    {
        var running = 0;
        var peak = 0;
        var slow = Tool.Create<Input, string>("lenta", "Lenta", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return "ok";
        });
        var model = new MockLanguageModel()
            .Returns(_ => Calls([.. Enumerable.Range(0, 12).Select(i => ($"c{i}", "lenta"))]))
            .ReturnsText("fin");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [slow],
            StopWhen = StopCondition.StepCountIs(3),
            MaxParallelToolCalls = limit,
            CancellationToken = TestContext.Current.CancellationToken,
        });

        Assert.Equal(12, result.Steps[0].ToolResults.Count);
        Assert.Equal([.. Enumerable.Range(0, 12).Select(i => $"c{i}")], result.Steps[0].ToolResults.Select(r => r.ToolCallId)); // orden conservado
        Assert.InRange(peak, 1, limit);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", MaxParallelToolCalls = 0 }));
        Assert.Equal(8, new GenerateTextOptions { Model = model }.MaxParallelToolCalls);
    }

    [Fact]
    public async Task ModelRepairHonoursCancellation()
    {
        var model = new MockLanguageModel().Returns(_ => new LanguageModelResult
        {
            Content = [new ToolCallPart("c1", "get_weather", JsonSerializer.SerializeToElement("{roto")) { InvalidInput = true }],
            FinishReason = FinishReason.ToolCalls,
            Usage = Usage.Empty,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var run = Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [Weather()],
            RepairToolCall = ToolCallRepair.WithModel(new HangingModel()),
            CancellationToken = cts.Token,
        });

        // Antes la reparación usaba CancellationToken.None y quedaba colgada para siempre.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    // -----------------------------------------------------------------
    // #7 JSON parcial lineal
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("""{"a":"he""", """{"a":"he"}""")]
    [InlineData("""{"a":1,"b""", """{"a":1}""")]
    [InlineData("""{"a":1,"b":""", """{"a":1}""")]
    [InlineData("""{"a":tr""", """{"a":true}""")]
    [InlineData("""{"a":nul""", """{"a":null}""")]
    [InlineData("""{"a":-""", """{}""")]
    [InlineData("""{"a":1.""", """{"a":1}""")]
    [InlineData("""{"a":12e""", """{"a":12}""")]
    [InlineData("""{"a":"x\""", """{"a":"x"}""")]
    [InlineData("""{"a":"x\u00""", """{"a":"x"}""")]
    [InlineData("""{"a":"xá""", """{"a":"xá"}""")]
    [InlineData("""{"a":[1,2""", """{"a":[1,2]}""")]
    [InlineData("""{"a":[1,""", """{"a":[1]}""")]
    [InlineData("""{"a":{"b":""", """{"a":{}}""")]
    [InlineData("""[{"a":1},{"b""", """[{"a":1},{}]""")]
    [InlineData("""{"k\"ey":"v""", """{"k\"ey":"v"}""")]
    [InlineData("```json\n{\"a\":1}\n```", """{"a":1}""")]
    [InlineData("""{"a":"x"} y luego texto {""", """{"a":"x"}""")]
    [InlineData("""{"a" 1}""", """{}""")]
    [InlineData("""{"a":[1}""", """{"a":[1]}""")]
    [InlineData("""[""", """[]""")]
    public void RepairsPartialJson(string partial, string expected)
    {
        Assert.Equal(expected, JsonUtil.ParsePartial(partial)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public void PartialJsonParsingIsLinearOnMalformedInput()
    {
        // Antes: con un prefijo mal formado se probaba cada prefijo más corto (~300 ms con 8k caracteres, por fragmento).
        var malformed = new StringBuilder("{\"items\":[");
        for (var i = 0; malformed.Length < 200_000; i++)
        {
            malformed.Append("{\"id\":").Append(i).Append(" \"name\":\"x\"},"); // falta una coma: JSON inválido
        }

        var text = malformed.ToString();
        JsonUtil.ParsePartial(text); // calentamiento
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            JsonUtil.ParsePartial(text);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"ParsePartial tardó {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal("""{"items":[{"id":0}]}""", JsonUtil.ParsePartial(text)!.ToJsonString());

        // Un prefijo largo y válido con el error al final se recorre una sola vez.
        var valid = new StringBuilder("{\"items\":[");
        var count = 0;
        for (; valid.Length < 200_000; count++)
        {
            valid.Append("{\"id\":").Append(count).Append(",\"name\":\"x\"},");
        }

        valid.Append("{\"id\":").Append(count).Append(" \"name\"");
        watch.Restart();
        var items = JsonUtil.ParsePartial(valid.ToString())!["items"]!.AsArray();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"ParsePartial tardó {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(count + 1, items.Count);
    }

    // -----------------------------------------------------------------
    // #9 Ciclos de alias
    // -----------------------------------------------------------------

    [Fact]
    public void AliasCyclesFailWithAClearErrorInsteadOfStackOverflow()
    {
        var registry = new ProviderRegistry()
            .Alias("a", "b")
            .Alias("b", "a")
            .Alias("propio", "propio")
            .Fallback("chat", "rapido")
            .Alias("rapido", "chat");

        Assert.Contains("a → b → a", Assert.Throws<InvalidOperationException>(() => registry.LanguageModel("a")).Message);
        Assert.Throws<InvalidOperationException>(() => registry.LanguageModel("propio"));
        Assert.Throws<InvalidOperationException>(() => registry.LanguageModel("chat"));

        // Un alias válido sigue funcionando después de un ciclo (el estado de resolución se limpia).
        registry.Register("mock", new MockProvider()).Alias("ok", "mock:m1");
        Assert.Equal("m1", registry.LanguageModel("ok").ModelId);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class HangingModel : ILanguageModel
    {
        public string Provider => "mock";

        public string ModelId => "colgado";

        public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("inalcanzable");
        }

        public IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MockProvider : IProvider
    {
        public string Name => "mock";

        public ILanguageModel LanguageModel(string modelId) => new MockLanguageModel(modelId);
    }
}
