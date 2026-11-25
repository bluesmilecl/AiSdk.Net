using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiSdk.Extensions.AI;
using Microsoft.Extensions.AI;
using MEAI = Microsoft.Extensions.AI;

namespace AiSdk.Tests;

public sealed record MeaiCityInfo(string Name, int Population);

public sealed record MeaiWeatherInput([property: Description("City")] string City);

public sealed record MeaiWeatherOutput(string City, int TemperatureC);

public class MicrosoftExtensionsAITests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AIFunction WeatherFunction(List<string>? calls = null) => AIFunctionFactory.Create(
        (string city) =>
        {
            calls?.Add(city);
            return $"Sunny in {city}";
        },
        "get_weather",
        "Gets the weather of a city");

    private static Tool WeatherTool() =>
        Tool.Create<MeaiWeatherInput, MeaiWeatherOutput>("get_weather", "Gets the weather", input => new MeaiWeatherOutput(input.City, 21));

    private static Dictionary<string, JsonElement> Signature(string key, string value) => new() { [key] = JsonSerializer.SerializeToElement(value) };

    // ---- Language model -> IChatClient ----

    [Fact]
    public async Task GetResponseMapsTextReasoningUsageFinishAndMetadata()
    {
        var model = new MockLanguageModel().Returns(_ => new LanguageModelResult
        {
            Content = [new ReasoningPart("Let me think"), new TextPart("Hello from AiSdk")],
            FinishReason = FinishReason.Stop,
            Usage = new Usage { InputTokens = 10, OutputTokens = 5, ReasoningTokens = 2, CachedInputTokens = 3 },
            Response = new ResponseMetadata("resp_1", "mock-model-2026", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
        });
        using var client = model.AsIChatClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        Assert.Equal("Hello from AiSdk", response.Text);
        var message = Assert.Single(response.Messages);
        Assert.Equal(MEAI.ChatRole.Assistant, message.Role);
        Assert.Equal("Let me think", Assert.IsType<TextReasoningContent>(message.Contents[0]).Text);
        Assert.Equal("resp_1", response.ResponseId);
        Assert.Equal("resp_1", message.MessageId);
        Assert.Equal("mock-model-2026", response.ModelId);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), response.CreatedAt);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(10, response.Usage!.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
        Assert.Equal(15, response.Usage.TotalTokenCount);
        Assert.Equal(2, response.Usage.ReasoningTokenCount);
        Assert.Equal(3, response.Usage.CachedInputTokenCount);
        Assert.IsType<LanguageModelResult>(response.RawRepresentation);

        var sent = Assert.Single(model.Requests).Messages;
        Assert.Equal(ChatRole.User, Assert.Single(sent).Role);
        Assert.Equal("Hi", sent[0].Text);
    }

    [Fact]
    public async Task GetResponseWithoutMetadataUsesModelId()
    {
        var model = new MockLanguageModel().Returns(_ => new LanguageModelResult { Content = [new TextPart("ok")] });
        using var client = model.AsIChatClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        Assert.Equal("mock-model", response.ModelId);
        Assert.Null(response.ResponseId);
        Assert.Null(response.FinishReason);
        Assert.Null(response.Usage);
    }

    [Theory]
    [InlineData(FinishReason.Stop, "stop")]
    [InlineData(FinishReason.Length, "length")]
    [InlineData(FinishReason.ToolCalls, "tool_calls")]
    [InlineData(FinishReason.ContentFilter, "content_filter")]
    [InlineData(FinishReason.Error, "error")]
    [InlineData(FinishReason.Other, "other")]
    [InlineData(FinishReason.Unknown, null)]
    public async Task MapsFinishReasons(FinishReason reason, string? expected)
    {
        var model = new MockLanguageModel().Returns(_ => new LanguageModelResult { Content = [new TextPart("x")], FinishReason = reason });
        using var client = model.AsIChatClient();

        var response = await client.GetResponseAsync("Hi", cancellationToken: Ct);

        Assert.Equal(expected, response.FinishReason?.Value);
    }

    [Fact]
    public async Task MapsMessagesAndChatOptionsToTheRequest()
    {
        var model = new MockLanguageModel().ReturnsText("ok");
        using var client = model.AsIChatClient();
        var schema = JsonElement.Parse("""{"type":"object","properties":{"answer":{"type":"string"}}}""");
        List<ChatMessage> messages =
        [
            new(MEAI.ChatRole.System, "You are helpful"),
            new(new MEAI.ChatRole("developer"), "Be brief"),
            new(MEAI.ChatRole.User,
            [
                new TextContent("Describe these"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
                new UriContent("https://example.test/cat.jpg", "image/jpeg"),
                new DataContent(new byte[] { 4, 5 }, "application/pdf") { Name = "report.pdf" },
                new UriContent("https://example.test/doc.pdf", "application/pdf"),
                new HostedFileContent("file_123") { MediaType = "image/jpeg" },
                new HostedFileContent("file_456") { Name = "notes.txt" },
            ]),
            new(MEAI.ChatRole.Assistant, "Previous answer"),
        ];

        await client.GetResponseAsync(messages, new ChatOptions
        {
            Instructions = "Answer in English",
            Temperature = 0.7f,
            TopP = 0.9f,
            TopK = 40,
            MaxOutputTokens = 100,
            FrequencyPenalty = 0.5f,
            PresencePenalty = 0.25f,
            Seed = 42,
            StopSequences = ["END"],
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "Answer", "The answer"),
            Tools = [WeatherFunction(), new HostedWebSearchTool()],
            ToolMode = ChatToolMode.RequireSpecific("get_weather"),
            AdditionalProperties = new() { ["strictJsonSchema"] = true },
        }, Ct);

        var request = Assert.Single(model.Requests);
        Assert.Equal(0.7, request.Temperature);
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(40, request.TopK);
        Assert.Equal(100, request.MaxOutputTokens);
        Assert.Equal(0.5, request.FrequencyPenalty);
        Assert.Equal(0.25, request.PresencePenalty);
        Assert.Equal(42, request.Seed);
        Assert.Equal(["END"], request.StopSequences!);
        Assert.Equal(ResponseFormatType.Json, request.ResponseFormat!.Type);
        Assert.Equal("Answer", request.ResponseFormat.Name);
        Assert.Equal("The answer", request.ResponseFormat.Description);
        Assert.Equal("string", request.ResponseFormat.Schema!.Value.GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());

        // Only function declarations reach the model; hosted tools are ignored.
        var tool = Assert.Single(request.Tools!);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Gets the weather of a city", tool.Description);
        Assert.True(tool.InputSchema.GetProperty("properties").TryGetProperty("city", out _));
        Assert.True(tool.Strict);
        Assert.Equal(ToolChoiceKind.Tool, request.ToolChoice!.Kind);
        Assert.Equal("get_weather", request.ToolChoice.ToolName);

        var sent = request.Messages;
        Assert.Equal([ChatRole.System, ChatRole.System, ChatRole.System, ChatRole.User, ChatRole.Assistant], sent.Select(m => m.Role));
        Assert.Equal("Answer in English", sent[0].Text);
        Assert.Equal("Be brief", sent[2].Text);
        var user = sent[3].Content;
        Assert.Equal("Describe these", Assert.IsType<TextPart>(user[0]).Text);
        var image = Assert.IsType<ImagePart>(user[1]);
        Assert.Equal([1, 2, 3], image.Data!);
        Assert.Equal("image/png", image.MediaType);
        var imageUrl = Assert.IsType<ImagePart>(user[2]);
        Assert.Equal(new Uri("https://example.test/cat.jpg"), imageUrl.Url);
        Assert.Equal("image/jpeg", imageUrl.MediaType);
        var pdf = Assert.IsType<FilePart>(user[3]);
        Assert.Equal([4, 5], pdf.Data!);
        Assert.Equal("application/pdf", pdf.MediaType);
        Assert.Equal("report.pdf", pdf.Filename);
        Assert.Equal(new Uri("https://example.test/doc.pdf"), Assert.IsType<FilePart>(user[4]).Url);
        var hostedImage = Assert.IsType<ImagePart>(user[5]);
        Assert.Equal("file_123", hostedImage.FileId);
        Assert.Equal("image/jpeg", hostedImage.MediaType);
        var hostedFile = Assert.IsType<FilePart>(user[6]);
        Assert.Equal("file_456", hostedFile.FileId);
        Assert.Equal("notes.txt", hostedFile.Filename);
        Assert.Equal("Previous answer", sent[4].Text);
    }

    [Theory]
    [InlineData("auto", ToolChoiceKind.Auto)]
    [InlineData("none", ToolChoiceKind.None)]
    [InlineData("any", ToolChoiceKind.Required)]
    public async Task MapsToolModes(string mode, ToolChoiceKind expected)
    {
        var model = new MockLanguageModel().ReturnsText("ok").ReturnsText("ok");
        using var client = model.AsIChatClient();
        ChatToolMode toolMode = mode switch
        {
            "auto" => ChatToolMode.Auto,
            "none" => ChatToolMode.None,
            _ => ChatToolMode.RequireAny,
        };

        await client.GetResponseAsync("Hi", new ChatOptions { Tools = [WeatherFunction()], ToolMode = toolMode }, Ct);
        await client.GetResponseAsync("Hi", new ChatOptions { ToolMode = toolMode }, Ct);

        Assert.Equal(expected, model.Requests[0].ToolChoice!.Kind);

        // Without tools there is nothing to choose from: no tool choice is sent.
        Assert.Null(model.Requests[1].Tools);
        Assert.Null(model.Requests[1].ToolChoice);
    }

    [Fact]
    public async Task RawRepresentationFactoryProvidesProviderOptionsHeadersAndSettings()
    {
        var model = new MockLanguageModel().ReturnsText("ok").ReturnsText("ok");
        using var client = model.AsIChatClient();
        var providerOptions = new ProviderOptions { ["mock"] = new() { ["reasoningEffort"] = "high" } };

        await client.GetResponseAsync("Hi", new ChatOptions { RawRepresentationFactory = _ => providerOptions }, Ct);
        await client.GetResponseAsync("Hi", new ChatOptions
        {
            Temperature = 0.2f,
            MaxOutputTokens = 50,
            RawRepresentationFactory = _ => new LanguageModelRequest
            {
                Messages = [ModelMessage.User("ignored")],
                Temperature = 1,
                Headers = new Dictionary<string, string> { ["X-Test"] = "1" },
            },
        }, Ct);

        Assert.Equal("high", (string?)model.Requests[0].ProviderOptions!.For("mock")!["reasoningEffort"]);
        var second = model.Requests[1];
        Assert.Equal(1, second.Temperature);
        Assert.Equal(50, second.MaxOutputTokens);
        Assert.Equal("1", second.Headers!["X-Test"]);
        Assert.Equal("Hi", Assert.Single(second.Messages).Text);
    }

    [Fact]
    public async Task ReturnsToolCallsWithoutExecutingThem()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel().ReturnsToolCall("get_weather", new { city = "Santiago" }, "call_1");
        using var client = model.AsIChatClient();

        var response = await client.GetResponseAsync("Weather?", new ChatOptions { Tools = [WeatherFunction(calls)] }, Ct);

        Assert.Empty(calls);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(response.Messages.Single().Contents));
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.False(call.InformationalOnly);
        Assert.Null(call.Exception);
        Assert.Equal("Santiago", call.Arguments!["city"]?.ToString());
    }

    [Fact]
    public async Task InvalidToolInputIsReportedOnTheFunctionCall()
    {
        var model = new MockLanguageModel().Returns(_ => new LanguageModelResult
        {
            Content = [new ToolCallPart("call_1", "get_weather", JsonSerializer.SerializeToElement("{\"city\":")) { InvalidInput = true }],
            FinishReason = FinishReason.ToolCalls,
        });
        using var client = model.AsIChatClient();

        var response = await client.GetResponseAsync("Weather?", cancellationToken: Ct);

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(response.Messages.Single().Contents));
        Assert.NotNull(call.Exception);
    }

    [Fact]
    public async Task FunctionInvokingChatClientRunsToolsAndSendsResultsBack()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Santiago" }, "call_1")
            .ReturnsText("It is sunny in Santiago");
        using var client = model.AsIChatClient().AsBuilder().UseFunctionInvocation().Build();

        var response = await client.GetResponseAsync("Weather in Santiago?", new ChatOptions { Tools = [WeatherFunction(calls)] }, Ct);

        Assert.Equal("It is sunny in Santiago", response.Text);
        Assert.Equal(["Santiago"], calls);
        Assert.Equal(2, model.Requests.Count);

        var first = model.Requests[0];
        Assert.Equal("get_weather", Assert.Single(first.Tools!).Name);
        Assert.Null(first.ToolChoice);

        var second = model.Requests[1].Messages;
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], second.Select(m => m.Role));
        var call = Assert.IsType<ToolCallPart>(Assert.Single(second[1].Content));
        Assert.Equal("call_1", call.ToolCallId);
        Assert.Equal("Santiago", call.Input.GetProperty("city").GetString());
        var result = Assert.IsType<ToolResultPart>(Assert.Single(second[2].Content));
        Assert.Equal("call_1", result.ToolCallId);
        Assert.Equal("get_weather", result.ToolName);
        Assert.False(result.IsError);
        Assert.Equal("Sunny in Santiago", result.Output.GetString());
    }

    [Fact]
    public async Task FunctionInvokingChatClientWorksWhenStreaming()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Lima" }, "call_1")
            .ReturnsText("Sunny in Lima, enjoy");
        using var client = model.AsIChatClient().AsBuilder().UseFunctionInvocation().Build();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync("Weather in Lima?", new ChatOptions { Tools = [WeatherFunction(calls)] }, Ct))
        {
            updates.Add(update);
        }

        Assert.Equal(["Lima"], calls);
        Assert.Equal("Sunny in Lima, enjoy", string.Concat(updates.Select(u => u.Text)));
        var result = Assert.IsType<ToolResultPart>(Assert.Single(model.Requests[1].Messages[2].Content));
        Assert.Equal("get_weather", result.ToolName);
        Assert.Equal("Sunny in Lima", result.Output.GetString());
    }

    [Fact]
    public async Task FailedFunctionsAreSentAsErrorResults()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("explode", new { }, "call_1")
            .ReturnsText("Sorry");
        using var client = model.AsIChatClient().AsBuilder().UseFunctionInvocation().Build();
        var explode = AIFunctionFactory.Create(string () => throw new InvalidOperationException("boom"), "explode");

        await client.GetResponseAsync("Go", new ChatOptions { Tools = [explode] }, Ct);

        var result = Assert.IsType<ToolResultPart>(Assert.Single(model.Requests[1].Messages[2].Content));
        Assert.True(result.IsError);
        Assert.Equal("explode", result.ToolName);
    }

    [Fact]
    public async Task ProviderExecutedToolsAreInformationalAndRoundTrip()
    {
        var model = new MockLanguageModel()
            .Returns(_ => new LanguageModelResult
            {
                Content =
                [
                    new ToolCallPart("srv_1", "web_search", JsonElement.Parse("""{"query":"dotnet"}""")) { ProviderExecuted = true },
                    new ToolResultPart("srv_1", "web_search", JsonElement.Parse("""[{"url":"https://dot.net"}]""")) { ProviderExecuted = true },
                    new SourcePart("src_1", "https://dot.net") { Title = ".NET", CitedText = "Free. Cross-platform." },
                    new RawProviderPart("mock", JsonElement.Parse("""{"type":"container"}""")),
                    new TextPart(".NET is free"),
                ],
                FinishReason = FinishReason.Stop,
            })
            .ReturnsText("Anything else?");
        var calls = new List<string>();
        using var client = model.AsIChatClient().AsBuilder().UseFunctionInvocation().Build();
        var history = new List<ChatMessage> { new(MEAI.ChatRole.User, "Search dotnet") };

        var response = await client.GetResponseAsync(history, new ChatOptions { Tools = [WeatherFunction(calls)] }, Ct);

        // FunctionInvokingChatClient does not run provider-executed calls.
        Assert.Single(model.Requests);
        Assert.Empty(calls);
        var contents = response.Messages.Single().Contents;
        Assert.True(Assert.IsType<FunctionCallContent>(contents[0]).InformationalOnly);
        Assert.IsType<FunctionResultContent>(contents[1]);
        var citation = Assert.IsType<CitationAnnotation>(Assert.Single(contents[2].Annotations!));
        Assert.Equal(new Uri("https://dot.net"), citation.Url);
        Assert.Equal(".NET", citation.Title);
        Assert.Equal("Free. Cross-platform.", citation.Snippet);
        Assert.Equal(".NET is free", response.Text);

        history.AddRange(response.Messages);
        history.Add(new ChatMessage(MEAI.ChatRole.User, "Thanks"));
        await client.GetResponseAsync(history, cancellationToken: Ct);

        var assistant = model.Requests[1].Messages[1];
        Assert.Equal(ChatRole.Assistant, assistant.Role);
        Assert.True(Assert.IsType<ToolCallPart>(assistant.Content[0]).ProviderExecuted);
        Assert.True(Assert.IsType<ToolResultPart>(assistant.Content[1]).ProviderExecuted);
        Assert.IsType<SourcePart>(assistant.Content[2]);
        Assert.Equal("mock", Assert.IsType<RawProviderPart>(assistant.Content[3]).Provider);
        Assert.Equal(".NET is free", Assert.IsType<TextPart>(assistant.Content[4]).Text);
        Assert.DoesNotContain(model.Requests[1].Messages, m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task StreamsTextUpdatesWithUsageAndFinishReason()
    {
        var model = new MockLanguageModel { StreamChunkSize = 3 }.Returns(_ => new LanguageModelResult
        {
            Content = [new TextPart("Hello from a stream")],
            FinishReason = FinishReason.Stop,
            Usage = new Usage { InputTokens = 7, OutputTokens = 4 },
            Response = new ResponseMetadata("resp_9", "mock-model-2026", DateTimeOffset.UnixEpoch),
        });
        using var client = model.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct))
        {
            updates.Add(update);
        }

        Assert.True(updates.Count > 2);
        Assert.All(updates, u => Assert.Equal(MEAI.ChatRole.Assistant, u.Role));
        Assert.All(updates, u => Assert.Equal("resp_9", u.ResponseId));
        Assert.All(updates, u => Assert.IsAssignableFrom<StreamPart>(u.RawRepresentation));
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);

        var response = updates.ToChatResponse();
        Assert.Equal("Hello from a stream", response.Text);
        Assert.Single(response.Messages);
        Assert.Equal("resp_9", response.ResponseId);
        Assert.Equal("mock-model-2026", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(7, response.Usage!.InputTokenCount);
        Assert.Equal(4, response.Usage.OutputTokenCount);
        Assert.Equal(11, response.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task StreamingErrorsAreThrown()
    {
        using var client = new ScriptedLanguageModel([new TextDeltaPart("0", "partial"), new ErrorPart(new InvalidOperationException("boom"))]).AsIChatClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct))
            {
                Assert.Equal("partial", update.Text);
            }
        });

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task ReasoningMetadataSurvivesStreamingCoalescingAndSerialization()
    {
        var model = new MockLanguageModel()
            .Returns(_ => new LanguageModelResult
            {
                Content =
                [
                    new ReasoningPart("Thinking hard") { ProviderMetadata = Signature("anthropic.signature", "sig-1") },
                    new TextPart("Done"),
                    new ToolCallPart("call_1", "get_weather", JsonElement.Parse("""{"city":"Quito"}""")) { ProviderMetadata = Signature("google.thoughtSignature", "ts-1") },
                ],
                FinishReason = FinishReason.ToolCalls,
            })
            .ReturnsText("Thanks");
        using var client = model.AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync("Hi", cancellationToken: Ct))
        {
            updates.Add(update);
        }

        var message = Assert.Single(updates.ToChatResponse().Messages);
        var reasoning = Assert.IsType<TextReasoningContent>(message.Contents[0]);
        Assert.Equal("Thinking hard", reasoning.Text);
        Assert.NotNull(reasoning.ProtectedData);

        // Persist the conversation as JSON (RawRepresentation is lost) and send it back.
        List<ChatMessage> history = [new(MEAI.ChatRole.User, "Hi"), message, new(MEAI.ChatRole.Tool, [new FunctionResultContent("call_1", "22C")])];
        var json = JsonSerializer.Serialize(history, AIJsonUtilities.DefaultOptions);
        var restored = JsonSerializer.Deserialize<List<ChatMessage>>(json, AIJsonUtilities.DefaultOptions)!;
        await client.GetResponseAsync(restored, cancellationToken: Ct);

        var sent = model.Requests[1].Messages;
        var assistant = sent[1].Content;
        var sentReasoning = Assert.IsType<ReasoningPart>(assistant[0]);
        Assert.Equal("Thinking hard", sentReasoning.Text);
        Assert.Equal("sig-1", sentReasoning.ProviderMetadata!["anthropic.signature"].GetString());
        Assert.Equal("Done", Assert.IsType<TextPart>(assistant[1]).Text);
        var sentCall = Assert.IsType<ToolCallPart>(assistant[2]);
        Assert.Equal("ts-1", sentCall.ProviderMetadata!["google.thoughtSignature"].GetString());
        Assert.Equal("Quito", sentCall.Input.GetProperty("city").GetString());
        var result = Assert.IsType<ToolResultPart>(Assert.Single(sent[2].Content));
        Assert.Equal("get_weather", result.ToolName);
        Assert.Equal("22C", result.Output.GetString());
    }

    [Fact]
    public async Task StructuredOutputUsesTheJsonSchemaResponseFormat()
    {
        var model = new MockLanguageModel().ReturnsText("""{"name":"Santiago","population":7000000}""");
        using var client = model.AsIChatClient();

        var response = await client.GetResponseAsync<MeaiCityInfo>("Largest city of Chile?", cancellationToken: Ct);

        Assert.Equal(new MeaiCityInfo("Santiago", 7000000), response.Result);
        var format = model.Requests[0].ResponseFormat!;
        Assert.Equal(ResponseFormatType.Json, format.Type);
        Assert.Contains("population", format.Schema!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationIsHonored()
    {
        var model = new ScriptedLanguageModel([new TextDeltaPart("0", "partial")]) { BlockAfterScript = true };
        using var client = model.AsIChatClient();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("Hi", cancellationToken: cancelled.Token));
        Assert.Equal(0, model.Calls);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = client.GetResponseAsync("Hi", cancellationToken: cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync("Hi", cancellationToken: streamCts.Token))
            {
                Assert.Equal("partial", update.Text);
                await streamCts.CancelAsync();
            }
        });
    }

    [Fact]
    public void ExposesMetadataAndTheUnderlyingModel()
    {
        var model = new MockLanguageModel("gpt-test", "openai");
        using var client = model.AsIChatClient(new Uri("https://api.example.test/v1"));

        var metadata = client.GetService<ChatClientMetadata>()!;
        Assert.Equal("openai", metadata.ProviderName);
        Assert.Equal("gpt-test", metadata.DefaultModelId);
        Assert.Equal(new Uri("https://api.example.test/v1"), metadata.ProviderUri);
        Assert.Same(model, client.GetService<ILanguageModel>());
        Assert.Same(model, client.GetService<MockLanguageModel>());
        Assert.Same(client, client.GetService<IChatClient>());
        Assert.IsType<LanguageModelChatClient>(client);
        Assert.Null(client.GetService<ChatClientMetadata>("key"));
        Assert.Null(client.GetService<IEmbeddingModel>());
    }

    // ---- Round trips between both directions ----

    [Fact]
    public void ConvertingBackReturnsTheOriginalObject()
    {
        var model = new MockLanguageModel();
        var client = model.AsIChatClient();
        Assert.Same(model, client.AsLanguageModel());

        // Overriding the identity creates a real adapter.
        Assert.IsType<ChatClientLanguageModel>(client.AsLanguageModel(modelId: "other"));

        using var pipeline = new PassThroughChatClient(client);
        var adapted = pipeline.AsLanguageModel();
        Assert.IsType<ChatClientLanguageModel>(adapted);
        Assert.Equal("mock", adapted.Provider);
        Assert.Equal("mock-model", adapted.ModelId);
        Assert.Same(pipeline, adapted.AsIChatClient());
    }

    [Fact]
    public async Task AiSdkToolLoopWorksThroughAChatClientPipeline()
    {
        var model = new MockLanguageModel()
            .Returns(_ => new LanguageModelResult
            {
                Content =
                [
                    new ReasoningPart("Need the weather") { ProviderMetadata = Signature("anthropic.signature", "sig-2") },
                    new ToolCallPart("call_7", "get_weather", JsonElement.Parse("""{"city":"Bogota"}""")),
                ],
                FinishReason = FinishReason.ToolCalls,
                Usage = new Usage { InputTokens = 3, OutputTokens = 2 },
            })
            .ReturnsText("21C in Bogota");
        using var pipeline = new PassThroughChatClient(model.AsIChatClient());

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = pipeline.AsLanguageModel(),
            Prompt = "Weather in Bogota?",
            Tools = [WeatherTool()],
            StopWhen = StopCondition.StepCountIs(5),
            ProviderOptions = new ProviderOptions { ["mock"] = new() { ["mode"] = "fast" } },
            Headers = new Dictionary<string, string> { ["X-Trace"] = "abc" },
            CancellationToken = Ct,
        });

        Assert.Equal("21C in Bogota", result.Text);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(FinishReason.ToolCalls, result.Steps[0].FinishReason);
        Assert.Equal(3, result.Steps[0].Usage.InputTokens);

        // Settings without a Microsoft.Extensions.AI equivalent cross the pipeline as the raw representation.
        var first = model.Requests[0];
        Assert.Equal("fast", (string?)first.ProviderOptions!.For("mock")!["mode"]);
        Assert.Equal("abc", first.Headers!["X-Trace"]);
        Assert.Equal("get_weather", Assert.Single(first.Tools!).Name);

        var second = model.Requests[1].Messages;
        var reasoning = Assert.IsType<ReasoningPart>(second[1].Content[0]);
        Assert.Equal("sig-2", reasoning.ProviderMetadata!["anthropic.signature"].GetString());
        var toolResult = Assert.IsType<ToolResultPart>(Assert.Single(second[2].Content));
        Assert.Equal("get_weather", toolResult.ToolName);
        Assert.Equal(21, toolResult.Output.GetProperty("temperatureC").GetInt32());
    }

    [Fact]
    public async Task AiSdkStreamingToolLoopKeepsReasoningMetadataThroughAPipeline()
    {
        var model = new MockLanguageModel()
            .Returns(_ => new LanguageModelResult
            {
                Content =
                [
                    new ReasoningPart("Need the weather") { ProviderMetadata = Signature("anthropic.signature", "sig-3") },
                    new ToolCallPart("call_8", "get_weather", JsonElement.Parse("""{"city":"Lima"}""")),
                ],
                FinishReason = FinishReason.ToolCalls,
            })
            .ReturnsText("21C in Lima");
        using var pipeline = new PassThroughChatClient(model.AsIChatClient());

        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = pipeline.AsLanguageModel(),
            Prompt = "Weather in Lima?",
            Tools = [WeatherTool()],
            StopWhen = StopCondition.StepCountIs(5),
            CancellationToken = Ct,
        });

        Assert.Equal("21C in Lima", await stream.Text);
        var second = model.Requests[1].Messages;
        var reasoning = Assert.IsType<ReasoningPart>(second[1].Content[0]);
        Assert.Equal("Need the weather", reasoning.Text);
        Assert.Equal("sig-3", reasoning.ProviderMetadata!["anthropic.signature"].GetString());
        Assert.Equal("call_8", Assert.IsType<ToolCallPart>(second[1].Content[1]).ToolCallId);
    }

    [Fact]
    public async Task ChatClientReasoningProtectedDataIsReplayed()
    {
        using var inner = new RecordingChatClient(
            new ChatResponse(new ChatMessage(MEAI.ChatRole.Assistant,
            [
                new TextReasoningContent("Hmm") { ProtectedData = "opaque-signature" },
                new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Cusco" }),
            ])),
            new ChatResponse(new ChatMessage(MEAI.ChatRole.Assistant, "21C in Cusco")));

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = inner.AsLanguageModel(),
            Prompt = "Weather in Cusco?",
            Tools = [WeatherTool()],
            StopWhen = StopCondition.StepCountIs(5),
            CancellationToken = Ct,
        });

        Assert.Equal("21C in Cusco", result.Text);
        var replayed = inner.Requests[1].SelectMany(m => m.Contents).OfType<TextReasoningContent>().Single();
        Assert.Equal("opaque-signature", replayed.ProtectedData);
        var toolResult = inner.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("call_1", toolResult.CallId);
    }

    [Fact]
    public async Task CallsExecutedByTheChatClientPipelineAreNotRunAgain()
    {
        var calls = new List<string>();
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Santiago" }, "call_1")
            .ReturnsText("Sunny in Santiago today");
        using var pipeline = model.AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(configure: f => f.AdditionalTools = [WeatherFunction(calls)])
            .Build();

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = pipeline.AsLanguageModel(),
            Prompt = "Weather in Santiago?",
            StopWhen = StopCondition.StepCountIs(5),
            CancellationToken = Ct,
        });

        Assert.Equal(["Santiago"], calls);
        Assert.Equal(FinishReason.Stop, result.FinishReason);
        Assert.Equal("Sunny in Santiago today", result.Text);
        var step = Assert.Single(result.Steps);
        Assert.True(Assert.IsType<ToolCallPart>(step.Content.First(p => p is ToolCallPart)).ProviderExecuted);
        Assert.True(Assert.IsType<ToolResultPart>(step.Content.First(p => p is ToolResultPart)).ProviderExecuted);
    }

    // ---- Embedding model -> IEmbeddingGenerator ----

    [Fact]
    public async Task EmbeddingGeneratorBatchesAndReportsUsage()
    {
        var model = new MockEmbeddingModel(dimensions: 4, maxEmbeddingsPerCall: 2);
        using var generator = model.AsIEmbeddingGenerator();

        var embeddings = await generator.GenerateAsync(["uno", "dos", "tres"], cancellationToken: Ct);

        Assert.Equal(2, model.Calls);
        Assert.Equal(3, embeddings.Count);
        Assert.All(embeddings, e => Assert.Equal(4, e.Vector.Length));
        Assert.All(embeddings, e => Assert.Equal("mock-embedding", e.ModelId));
        Assert.Equal(10, embeddings.Usage!.InputTokenCount);
        Assert.Equal(10, embeddings.Usage.TotalTokenCount);

        var direct = await model.EmbedAsync(["dos"], Ct);
        Assert.Equal(direct.Embeddings[0], embeddings[1].Vector.ToArray());

        var vector = await generator.GenerateVectorAsync("hola", cancellationToken: Ct);
        Assert.Equal(4, vector.Length);

        var empty = await generator.GenerateAsync([], cancellationToken: Ct);
        Assert.Empty(empty);
        Assert.Null(empty.Usage);
        Assert.Equal(4, model.Calls);
    }

    [Fact]
    public async Task EmbeddingGeneratorExposesMetadataAndConvertsBack()
    {
        var model = new MockEmbeddingModel();
        using var generator = model.AsIEmbeddingGenerator(new Uri("https://embeddings.example.test"));

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>()!;
        Assert.Equal("mock", metadata.ProviderName);
        Assert.Equal("mock-embedding", metadata.DefaultModelId);
        Assert.Equal(new Uri("https://embeddings.example.test"), metadata.ProviderUri);
        Assert.Same(model, generator.GetService<IEmbeddingModel>());
        Assert.Same(generator, generator.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Null(generator.GetService<EmbeddingGeneratorMetadata>("key"));
        Assert.Same(model, generator.AsEmbeddingModel());

        using var pipeline = new PassThroughEmbeddingGenerator(generator);
        var adapted = pipeline.AsEmbeddingModel();
        Assert.IsType<EmbeddingGeneratorModel>(adapted);
        Assert.Equal("mock-embedding", adapted.ModelId);
        Assert.Same(pipeline, adapted.AsIEmbeddingGenerator());
        var result = await adapted.EmbedAsync(["a", "bc"], Ct);
        Assert.Equal(2, result.Embeddings.Count);
        Assert.Equal(3, result.Usage!.Tokens);
    }

    [Fact]
    public async Task CancelledEmbeddingRequestsDoNotCallTheModel()
    {
        var model = new MockEmbeddingModel();
        using var generator = model.AsIEmbeddingGenerator();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(["a"], cancellationToken: cts.Token));
        Assert.Equal(0, model.Calls);
    }

    // ---- Test doubles ----

    private sealed class PassThroughChatClient(IChatClient inner) : DelegatingChatClient(inner);

    private sealed class PassThroughEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner)
        : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner);

    /// <summary>Streams a fixed list of parts and can then block until cancelled.</summary>
    private sealed class ScriptedLanguageModel(IReadOnlyList<StreamPart> parts) : ILanguageModel
    {
        public string Provider => "scripted";

        public string ModelId => "scripted-model";

        public bool BlockAfterScript { get; init; }

        public int Calls { get; private set; }

        public async Task<LanguageModelResult> GenerateAsync(LanguageModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }

        public async IAsyncEnumerable<StreamPart> StreamAsync(LanguageModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            foreach (var part in parts)
            {
                yield return part;
            }

            if (BlockAfterScript)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    /// <summary>Chat client that returns fixed responses and records the messages it receives.</summary>
    private sealed class RecordingChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(_responses.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("recording", null, "recording-model") : null;

        public void Dispose()
        {
        }
    }
}
