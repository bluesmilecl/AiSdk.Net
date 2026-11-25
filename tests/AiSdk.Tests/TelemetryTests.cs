using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AiSdk.Internal;

namespace AiSdk.Tests;

/// <summary>
/// Captures the SDK spans and metrics of one test. Other tests run in parallel and also emit telemetry, so spans are
/// filtered by the trace of a root activity started here, and metrics by a model id unique to the test.
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private const string TestSourceName = "AiSdk.Tests.Telemetry";
    private static readonly ActivitySource TestSource = new(TestSourceName);

    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;
    private readonly Activity _root;

    public TelemetryCapture()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is AiTelemetry.SourceName or TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AiTelemetry.MeterName)
                {
                    Instruments[instrument.Name] = instrument;
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _meterListener.Start();

        _root = TestSource.StartActivity("telemetry-test")!;
    }

    public ConcurrentDictionary<string, Instrument> Instruments { get; } = new();

    public ActivitySpanId RootSpanId => _root.SpanId;

    /// <summary>Spans of this test (finished), in start order.</summary>
    public IReadOnlyList<Activity> Spans => _activities
        .Where(a => a.TraceId == _root.TraceId && a != _root)
        .OrderBy(a => a.StartTimeUtc)
        .ToList();

    public Activity Span(string name) => Assert.Single(Spans, s => s.DisplayName == name);

    /// <summary>Measurements of <paramref name="instrument"/> for the model of this test.</summary>
    public IReadOnlyList<(double Value, Dictionary<string, object?> Tags)> Measurements(string instrument, string model) => _measurements
        .Where(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault("gen_ai.request.model"), model))
        .Select(m => (m.Value, m.Tags))
        .ToList();

    public void Dispose()
    {
        _root.Dispose();
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
}

public class TelemetryTests
{
    private static string UniqueModel() => $"model-{Guid.NewGuid():N}";

    private static LanguageModelResult TextResult(string text, string? responseId = null, string? responseModel = null) => new()
    {
        Content = [new TextPart(text)],
        FinishReason = FinishReason.Stop,
        Usage = new Usage { InputTokens = 10, OutputTokens = 5 },
        Response = responseId is null ? null : new ResponseMetadata(responseId, responseModel, DateTimeOffset.UtcNow),
    };

    private static Tool WeatherTool(Func<WeatherInput, WeatherOutput>? run = null) =>
        Tool.Create<WeatherInput, WeatherOutput>("get_weather", "Obtiene el clima", run ?? (input => new WeatherOutput(input.City, 21)));

    [Fact]
    public async Task GenerateTextEmitsLoopAndChatSpansWithGenAiAttributes()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var model = new MockLanguageModel(modelId, "openai").Returns(_ => TextResult("Hola", "resp_123", $"{modelId}-2026-01-01"));

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "Saluda",
            Temperature = 0.2,
            TopP = 0.9,
            TopK = 40,
            MaxOutputTokens = 100,
            Seed = 7,
            StopSequences = ["FIN"],
            CancellationToken = TestContext.Current.CancellationToken,
        });

        var root = telemetry.Span("invoke_generate_text");
        Assert.Equal(ActivityKind.Internal, root.Kind);
        Assert.Equal(telemetry.RootSpanId, root.ParentSpanId);
        Assert.Equal("invoke_generate_text", root.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("openai", root.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(modelId, root.GetTagItem("gen_ai.request.model"));
        Assert.Equal(["stop"], Assert.IsType<string[]>(root.GetTagItem("gen_ai.response.finish_reasons")));
        Assert.Equal(10, root.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(ActivityStatusCode.Unset, root.Status);

        var chat = telemetry.Span($"chat {modelId}");
        Assert.Equal(ActivityKind.Client, chat.Kind);
        Assert.Equal(root.SpanId, chat.ParentSpanId);
        Assert.Equal("chat", chat.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("openai", chat.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(modelId, chat.GetTagItem("gen_ai.request.model"));
        Assert.Equal(0.2, chat.GetTagItem("gen_ai.request.temperature"));
        Assert.Equal(0.9, chat.GetTagItem("gen_ai.request.top_p"));
        Assert.Equal(40, chat.GetTagItem("gen_ai.request.top_k"));
        Assert.Equal(100, chat.GetTagItem("gen_ai.request.max_tokens"));
        Assert.Equal(7, chat.GetTagItem("gen_ai.request.seed"));
        Assert.Equal(["FIN"], Assert.IsType<string[]>(chat.GetTagItem("gen_ai.request.stop_sequences")));
        Assert.Null(chat.GetTagItem("gen_ai.request.presence_penalty"));
        Assert.Null(chat.GetTagItem("gen_ai.request.stream"));
        Assert.Equal("resp_123", chat.GetTagItem("gen_ai.response.id"));
        Assert.Equal($"{modelId}-2026-01-01", chat.GetTagItem("gen_ai.response.model"));
        Assert.Equal(["stop"], Assert.IsType<string[]>(chat.GetTagItem("gen_ai.response.finish_reasons")));
        Assert.Equal(10, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(5, chat.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(ActivityStatusCode.Unset, chat.Status);

        // Los atributos ai.* anteriores a 1.0 ya no existen.
        Assert.DoesNotContain(telemetry.Spans.SelectMany(s => s.TagObjects), t => t.Key.StartsWith("ai.", StringComparison.Ordinal));
        Assert.Equal(2, telemetry.Spans.Count);
    }

    [Fact]
    public async Task AgentToolLoopEmitsInvokeAgentWithChatAndExecuteToolChildren()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var model = new MockLanguageModel(modelId, "anthropic")
            .ReturnsToolCall("get_weather", new { city = "Lima" }, "call_1")
            .ReturnsText("21°C en Lima");
        var agent = new ToolLoopAgent(new AgentSettings
        {
            Name = "weather-agent",
            Description = "Responde sobre el clima",
            Model = model,
            Tools = [WeatherTool()],
            Temperature = 0.5,
        });

        await agent.GenerateAsync("¿Clima en Lima?", TestContext.Current.CancellationToken);

        var root = telemetry.Span("invoke_agent weather-agent");
        Assert.Equal(ActivityKind.Internal, root.Kind);
        Assert.Equal("invoke_agent", root.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("weather-agent", root.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("Responde sobre el clima", root.GetTagItem("gen_ai.agent.description"));
        Assert.Equal("anthropic", root.GetTagItem("gen_ai.provider.name"));
        Assert.Equal(0.5, root.GetTagItem("gen_ai.request.temperature"));
        Assert.Equal(20, root.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(10, root.GetTagItem("gen_ai.usage.output_tokens"));

        var children = telemetry.Spans.Where(s => s.ParentSpanId == root.SpanId).Select(s => s.DisplayName).ToList();
        Assert.Equal([$"chat {modelId}", "execute_tool get_weather", $"chat {modelId}"], children);

        var tool = telemetry.Span("execute_tool get_weather");
        Assert.Equal(ActivityKind.Internal, tool.Kind);
        Assert.Equal("execute_tool", tool.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("get_weather", tool.GetTagItem("gen_ai.tool.name"));
        Assert.Equal("call_1", tool.GetTagItem("gen_ai.tool.call.id"));
        Assert.Equal("function", tool.GetTagItem("gen_ai.tool.type"));
        Assert.Equal("Obtiene el clima", tool.GetTagItem("gen_ai.tool.description"));
        Assert.Equal("weather-agent", tool.GetTagItem("gen_ai.agent.name"));
        Assert.Equal(ActivityStatusCode.Unset, tool.Status);

        var chats = telemetry.Spans.Where(s => s.DisplayName == $"chat {modelId}").ToList();
        Assert.Equal(["tool_calls"], Assert.IsType<string[]>(chats[0].GetTagItem("gen_ai.response.finish_reasons")));
        Assert.Equal(["stop"], Assert.IsType<string[]>(chats[1].GetTagItem("gen_ai.response.finish_reasons")));
    }

    [Fact]
    public async Task UnnamedToolLoopAgentReportsInvokeAgentWithoutName()
    {
        using var telemetry = new TelemetryCapture();
        var agent = new ToolLoopAgent(new AgentSettings { Model = new MockLanguageModel(UniqueModel()).ReturnsText("ok") });

        await agent.GenerateAsync("x", TestContext.Current.CancellationToken);

        var root = telemetry.Span("invoke_agent");
        Assert.Equal("invoke_agent", root.GetTagItem("gen_ai.operation.name"));
        Assert.Null(root.GetTagItem("gen_ai.agent.name"));
    }

    [Fact]
    public async Task SubAgentRunsInsideTheExecuteToolSpanOfItsParent()
    {
        using var telemetry = new TelemetryCapture();
        var researcher = new ToolLoopAgent(new AgentSettings
        {
            Name = "researcher",
            Model = new MockLanguageModel(UniqueModel()).ReturnsText("hallazgos"),
        });
        var lead = new ToolLoopAgent(new AgentSettings
        {
            Name = "lead",
            Model = new MockLanguageModel(UniqueModel())
                .ReturnsToolCall("researcher", new { task = "investiga" })
                .ReturnsText("listo"),
            Tools = [researcher.AsTool()],
        });

        await lead.GenerateAsync("x", TestContext.Current.CancellationToken);

        var leadSpan = telemetry.Span("invoke_agent lead");
        var toolSpan = telemetry.Span("execute_tool researcher");
        var researcherSpan = telemetry.Span("invoke_agent researcher");
        Assert.Equal(leadSpan.SpanId, toolSpan.ParentSpanId);
        Assert.Equal(toolSpan.SpanId, researcherSpan.ParentSpanId);
        Assert.Contains(telemetry.Spans, s => s.ParentSpanId == researcherSpan.SpanId && s.DisplayName.StartsWith("chat ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailingToolMarksOnlyItsExecuteToolSpanAsError()
    {
        using var telemetry = new TelemetryCapture();
        var model = new MockLanguageModel(UniqueModel())
            .ReturnsToolCall("get_weather", new { city = "Lima" })
            .ReturnsText("No pude consultar el clima");

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [WeatherTool(_ => throw new InvalidOperationException("servicio caído"))],
            StopWhen = StopCondition.StepCountIs(3),
            CancellationToken = TestContext.Current.CancellationToken,
        });

        var tool = telemetry.Span("execute_tool get_weather");
        Assert.Equal(ActivityStatusCode.Error, tool.Status);
        Assert.Equal("servicio caído", tool.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, tool.GetTagItem("error.type"));

        // El error se entregó al modelo y el bucle terminó bien.
        var root = telemetry.Span("invoke_generate_text");
        Assert.Equal(ActivityStatusCode.Unset, root.Status);
        Assert.Null(root.GetTagItem("error.type"));
    }

    [Fact]
    public async Task ProviderErrorSetsErrorTypeOnSpansAndDurationMetric()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var model = new MockLanguageModel(modelId, "openai").ReturnsError(new ApiCallException("solicitud inválida", 400, null, null));

        await Assert.ThrowsAsync<ApiCallException>(() => Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            CancellationToken = TestContext.Current.CancellationToken,
        }));

        var chat = telemetry.Span($"chat {modelId}");
        Assert.Equal(ActivityStatusCode.Error, chat.Status);
        Assert.Equal("solicitud inválida", chat.StatusDescription);
        Assert.Equal("400", chat.GetTagItem("error.type"));
        var root = telemetry.Span("invoke_generate_text");
        Assert.Equal(ActivityStatusCode.Error, root.Status);
        Assert.Equal("400", root.GetTagItem("error.type"));

        var duration = Assert.Single(telemetry.Measurements("gen_ai.client.operation.duration", modelId));
        Assert.Equal("400", duration.Tags["error.type"]);
        Assert.Empty(telemetry.Measurements("gen_ai.client.token.usage", modelId));
    }

    [Fact]
    public async Task ChatRecordsDurationAndTokenUsageMetrics()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var model = new MockLanguageModel(modelId, "google").Returns(_ => TextResult("ok", "resp_1", "gemini-x-001"));

        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", CancellationToken = TestContext.Current.CancellationToken });

        var duration = Assert.Single(telemetry.Measurements("gen_ai.client.operation.duration", modelId));
        Assert.True(duration.Value > 0);
        Assert.Equal("chat", duration.Tags["gen_ai.operation.name"]);
        Assert.Equal("gcp.gemini", duration.Tags["gen_ai.provider.name"]);
        Assert.Equal("gemini-x-001", duration.Tags["gen_ai.response.model"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));

        var tokens = telemetry.Measurements("gen_ai.client.token.usage", modelId);
        Assert.Equal(2, tokens.Count);
        Assert.Equal(10, Assert.Single(tokens, t => Equals(t.Tags["gen_ai.token.type"], "input")).Value);
        Assert.Equal(5, Assert.Single(tokens, t => Equals(t.Tags["gen_ai.token.type"], "output")).Value);
        Assert.All(tokens, t => Assert.Equal("gcp.gemini", t.Tags["gen_ai.provider.name"]));

        var durationInstrument = Assert.IsType<Histogram<double>>(telemetry.Instruments["gen_ai.client.operation.duration"]);
        Assert.Equal("s", durationInstrument.Unit);
        Assert.Equal([0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92], durationInstrument.Advice!.HistogramBucketBoundaries!);
        var tokenInstrument = Assert.IsType<Histogram<int>>(telemetry.Instruments["gen_ai.client.token.usage"]);
        Assert.Equal("{token}", tokenInstrument.Unit);
        Assert.Equal(67108864, tokenInstrument.Advice!.HistogramBucketBoundaries![^1]);
    }

    [Fact]
    public async Task StreamTextEmitsStreamingChatSpanBeforeTheResultCompletes()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var model = new MockLanguageModel(modelId).ReturnsText("Hola mundo en streaming");

        var stream = Ai.StreamText(new GenerateTextOptions { Model = model, Prompt = "x", CancellationToken = TestContext.Current.CancellationToken });
        await stream.Text;

        var root = telemetry.Span("invoke_stream_text");
        Assert.Equal(telemetry.RootSpanId, root.ParentSpanId);
        Assert.Equal(["stop"], Assert.IsType<string[]>(root.GetTagItem("gen_ai.response.finish_reasons")));
        var chat = telemetry.Span($"chat {modelId}");
        Assert.Equal(root.SpanId, chat.ParentSpanId);
        Assert.True(chat.GetTagItem("gen_ai.request.stream") is true);
        Assert.True((double)chat.GetTagItem("gen_ai.response.time_to_first_chunk")! >= 0);
        Assert.Equal(5, chat.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Single(telemetry.Measurements("gen_ai.client.operation.duration", modelId));
    }

    [Fact]
    public async Task GenerateObjectReportsItsEntryPointAndJsonOutput()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var model = new MockLanguageModel(modelId).ReturnsText("""{"city":"Lima","temperatureC":21}""");

        await Ai.GenerateObjectAsync<WeatherOutput>(new GenerateObjectOptions { Model = model, Prompt = "x", CancellationToken = TestContext.Current.CancellationToken });

        telemetry.Span("invoke_generate_object");
        Assert.Equal("json", telemetry.Span($"chat {modelId}").GetTagItem("gen_ai.output.type"));
    }

    [Fact]
    public async Task EmbeddingsEmitOneClientSpanAndMetricsPerCall()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();

        await Ai.EmbedManyAsync(new NamedEmbeddingModel(modelId), ["uno", "dos", "tres"], TestContext.Current.CancellationToken);

        var spans = telemetry.Spans.Where(s => s.DisplayName == $"embeddings {modelId}").ToList();
        Assert.Equal(2, spans.Count);
        Assert.All(spans, s =>
        {
            Assert.Equal(ActivityKind.Client, s.Kind);
            Assert.Equal("embeddings", s.GetTagItem("gen_ai.operation.name"));
            Assert.Equal("cohere", s.GetTagItem("gen_ai.provider.name"));
            Assert.Equal(modelId, s.GetTagItem("gen_ai.request.model"));
        });
        Assert.Equal(6, spans[0].GetTagItem("gen_ai.usage.input_tokens"));

        Assert.Equal(2, telemetry.Measurements("gen_ai.client.operation.duration", modelId).Count);
        var tokens = telemetry.Measurements("gen_ai.client.token.usage", modelId);
        Assert.Equal([6.0, 4.0], tokens.Select(t => t.Value));
        Assert.All(tokens, t => Assert.Equal("input", t.Tags["gen_ai.token.type"]));
    }

    [Fact]
    public async Task SessionReportsItsConversationId()
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();
        var agent = new ToolLoopAgent(new AgentSettings { Name = "support", Model = new MockLanguageModel(modelId).ReturnsText("hola") });

        await agent.CreateSession("conv-42").SendAsync("hola", TestContext.Current.CancellationToken);

        Assert.Equal("conv-42", telemetry.Span("invoke_agent support").GetTagItem("gen_ai.conversation.id"));
        Assert.Equal("conv-42", telemetry.Span($"chat {modelId}").GetTagItem("gen_ai.conversation.id"));
    }

    [Theory]
    [InlineData("openai", "openai")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("google", "gcp.gemini")]
    [InlineData("bedrock", "aws.bedrock")]
    [InlineData("mistral", "mistral")]
    [InlineData("cohere", "cohere")]
    [InlineData("xai", "x_ai")]
    [InlineData("my-gateway", "my-gateway")]
    public async Task ProviderIdsMapToWellKnownProviderNames(string provider, string expected)
    {
        using var telemetry = new TelemetryCapture();
        var modelId = UniqueModel();

        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel(modelId, provider).ReturnsText("ok"),
            Prompt = "x",
            CancellationToken = TestContext.Current.CancellationToken,
        });

        Assert.Equal(expected, telemetry.Span($"chat {modelId}").GetTagItem("gen_ai.provider.name"));
    }

    [Fact]
    public async Task MessageContentIsNotRecordedByDefault()
    {
        using var telemetry = new TelemetryCapture();
        var model = new MockLanguageModel(UniqueModel())
            .ReturnsToolCall("get_weather", new { city = "SECRET-ARGUMENT" })
            .ReturnsText("SECRET-RESPONSE");
        var agent = new ToolLoopAgent(new AgentSettings
        {
            Name = "assistant",
            Instructions = "SECRET-INSTRUCTIONS",
            Model = model,
            Tools = [WeatherTool(input => new WeatherOutput("SECRET-RESULT " + input.City, 21))],
        });

        await agent.GenerateAsync("SECRET-PROMPT", TestContext.Current.CancellationToken);

        Assert.Equal(4, telemetry.Spans.Count);
        foreach (var span in telemetry.Spans)
        {
            Assert.DoesNotContain("SECRET", span.DisplayName, StringComparison.Ordinal);
            Assert.Empty(span.Events);
            foreach (var tag in span.TagObjects)
            {
                var value = tag.Value is string[] values ? string.Join(",", values) : Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.DoesNotContain("SECRET", value, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(span.TagObjects, t => t.Key is "gen_ai.input.messages" or "gen_ai.output.messages" or "gen_ai.system_instructions"
                or "gen_ai.tool.call.arguments" or "gen_ai.tool.call.result" or "gen_ai.tool.definitions");
        }
    }

    [Fact]
    public void SourceAndMeterUseThePackageVersion()
    {
        using var telemetry = new TelemetryCapture();
        var informational = typeof(Ai).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var expected = informational.Split('+')[0];

        Assert.Equal(expected, AiTelemetry.ActivitySource.Version);
        Assert.Equal(AiTelemetry.SourceName, AiTelemetry.ActivitySource.Name);
        Assert.Equal(expected, telemetry.Instruments["gen_ai.client.operation.duration"].Meter.Version);
        Assert.Equal(AiTelemetry.MeterName, telemetry.Instruments["gen_ai.client.token.usage"].Meter.Name);
    }

    private sealed class NamedEmbeddingModel(string modelId) : IEmbeddingModel
    {
        private readonly MockEmbeddingModel _inner = new(dimensions: 4);

        public string Provider => "cohere";

        public string ModelId => modelId;

        public int? MaxEmbeddingsPerCall => 2;

        public Task<EmbeddingModelResult> EmbedAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default) =>
            _inner.EmbedAsync(values, cancellationToken);
    }
}
