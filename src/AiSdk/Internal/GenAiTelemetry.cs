using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;

namespace AiSdk.Internal;

/// <summary>Agent reported on the <c>invoke_agent</c> span (<c>gen_ai.agent.name</c>, <c>gen_ai.agent.description</c>).</summary>
internal sealed record TelemetryAgent(string? Name, string? Description);

/// <summary>
/// Spans and metrics that follow the OpenTelemetry semantic conventions for generative AI (<c>gen_ai.*</c>).
/// Message content (instructions, prompts, responses, tool arguments and results) is never recorded.
/// </summary>
internal static class GenAiTelemetry
{
    internal const string Chat = "chat";
    internal const string Embeddings = "embeddings";
    internal const string ExecuteTool = "execute_tool";
    internal const string InvokeAgent = "invoke_agent";

    private const string OperationNameKey = "gen_ai.operation.name";
    private const string ProviderNameKey = "gen_ai.provider.name";
    private const string RequestModelKey = "gen_ai.request.model";
    private const string ResponseModelKey = "gen_ai.response.model";
    private const string ErrorTypeKey = "error.type";

    /// <summary>Package version (informational version without build metadata): the instrumentation scope version.</summary>
    internal static readonly string Version = GetVersion();

    private static readonly Meter Meter = new(AiTelemetry.MeterName, Version);

    // Límites de los buckets recomendados por la especificación.
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration",
        "s",
        "GenAI operation duration.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92] });

    private static readonly Histogram<int> TokenUsage = Meter.CreateHistogram<int>(
        "gen_ai.client.token.usage",
        "{token}",
        "Number of input and output tokens used.",
        advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864] });

    private static readonly Dictionary<string, string> WellKnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = "openai",
        ["azure"] = "azure.ai.openai",
        ["azure-openai"] = "azure.ai.openai",
        ["anthropic"] = "anthropic",
        ["google"] = "gcp.gemini",
        ["gemini"] = "gcp.gemini",
        ["vertex"] = "gcp.vertex_ai",
        ["google-vertex"] = "gcp.vertex_ai",
        ["bedrock"] = "aws.bedrock",
        ["amazon-bedrock"] = "aws.bedrock",
        ["mistral"] = "mistral",
        ["cohere"] = "cohere",
        ["deepseek"] = "deepseek",
        ["groq"] = "groq",
        ["xai"] = "x_ai",
        ["x-ai"] = "x_ai",
        ["perplexity"] = "perplexity",
        ["ollama"] = "ollama",
        ["moonshot"] = "moonshot_ai",
        ["moonshotai"] = "moonshot_ai",
        ["watsonx"] = "ibm.watsonx.ai",
    };

    /// <summary>
    /// <c>gen_ai.provider.name</c> for an AiSdk provider id: the well-known value when there is one
    /// (<c>google</c> is <c>gcp.gemini</c>, <c>bedrock</c> is <c>aws.bedrock</c>...), otherwise the id itself.
    /// </summary>
    internal static string ProviderName(string provider) =>
        WellKnownProviders.TryGetValue(provider, out var name) ? name : provider;

    /// <summary>
    /// <c>error.type</c>: the HTTP status code of a provider error (<c>"429"</c>), or the full name of the exception type.
    /// </summary>
    internal static string ErrorType(Exception error) => error switch
    {
        ApiCallException api => api.StatusCode.ToString(CultureInfo.InvariantCulture),
        RetryException { InnerException: { } inner } => ErrorType(inner),
        _ => error.GetType().FullName ?? "_OTHER",
    };

    internal static string FinishReasonName(FinishReason reason) => reason switch
    {
        FinishReason.Stop => "stop",
        FinishReason.Length => "length",
        FinishReason.ContentFilter => "content_filter",
        FinishReason.ToolCalls => "tool_calls",
        FinishReason.Error => "error",
        FinishReason.Other => "other",
        _ => "unknown",
    };

    /// <summary>Marks a span as failed: <c>error.type</c> and status <see cref="ActivityStatusCode.Error"/> with the exception message.</summary>
    internal static void SetError(Activity? activity, Exception error)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ErrorTypeKey, ErrorType(error));
        activity.SetStatus(ActivityStatusCode.Error, error.Message);
    }

    /// <summary>
    /// Starts the span of a whole <c>GenerateText</c>/<c>StreamText</c> loop: <c>invoke_agent {gen_ai.agent.name}</c> when an
    /// agent runs it, otherwise <c>invoke_{entrypoint}</c> (<c>invoke_generate_text</c>, <c>invoke_stream_text</c>...).
    /// </summary>
    internal static Activity? StartInvocation(GenerateTextOptions o, bool stream)
    {
        if (!AiTelemetry.ActivitySource.HasListeners())
        {
            return null;
        }

        var agent = o.TelemetryAgent;
        var operation = agent is null ? "invoke_" + (o.TelemetryEntrypoint ?? (stream ? "stream_text" : "generate_text")) : InvokeAgent;
        List<KeyValuePair<string, object?>> tags =
        [
            new(OperationNameKey, operation),
            new(ProviderNameKey, ProviderName(o.Model.Provider)),
            new(RequestModelKey, o.Model.ModelId),
        ];
        if (agent?.Name is { Length: > 0 } agentName)
        {
            tags.Add(new("gen_ai.agent.name", agentName));
        }

        var activity = AiTelemetry.ActivitySource.StartActivity(SpanName(operation, agent?.Name), ActivityKind.Internal, default(ActivityContext), tags);
        if (activity is { IsAllDataRequested: true })
        {
            SetTagIfNotEmpty(activity, "gen_ai.agent.description", agent?.Description);
            SetTagIfNotEmpty(activity, "gen_ai.conversation.id", o.ConversationId);
            if (agent is not null)
            {
                SetRequestTags(activity, o.MaxOutputTokens, o.Temperature, o.TopP, o.TopK, o.PresencePenalty, o.FrequencyPenalty, o.StopSequences, o.Seed);
                if (o.Output?.ResponseFormat is { Type: ResponseFormatType.Json })
                {
                    activity.SetTag("gen_ai.output.type", "json");
                }
            }
        }

        return activity;
    }

    /// <summary>Final finish reason and total token usage of the loop.</summary>
    internal static void SetInvocationResult(Activity? activity, GenerateTextResult result)
    {
        if (activity is not { IsAllDataRequested: true })
        {
            return;
        }

        activity.SetTag("gen_ai.response.finish_reasons", new[] { FinishReasonName(result.FinishReason) });
        SetUsageTags(activity, result.TotalUsage, breakdown: false);
    }

    /// <summary>Starts a model call span (<c>chat {gen_ai.request.model}</c>) and its metrics; null when nobody listens.</summary>
    internal static ClientOperation? StartChat(ILanguageModel model, LanguageModelRequest request, bool stream, string? conversationId)
    {
        var operation = StartClientOperation(Chat, model.Provider, model.ModelId);
        if (operation?.Activity is { IsAllDataRequested: true } activity)
        {
            SetRequestTags(activity, request.MaxOutputTokens, request.Temperature, request.TopP, request.TopK, request.PresencePenalty, request.FrequencyPenalty, request.StopSequences, request.Seed);
            if (request.ResponseFormat is { Type: ResponseFormatType.Json })
            {
                activity.SetTag("gen_ai.output.type", "json");
            }

            if (stream)
            {
                activity.SetTag("gen_ai.request.stream", true);
            }

            SetTagIfNotEmpty(activity, "gen_ai.conversation.id", conversationId);
        }

        return operation;
    }

    /// <summary>Starts an embeddings call span (<c>embeddings {gen_ai.request.model}</c>) and its metrics; null when nobody listens.</summary>
    internal static ClientOperation? StartEmbeddings(IEmbeddingModel model) => StartClientOperation(Embeddings, model.Provider, model.ModelId);

    /// <summary>Starts the span of a local tool execution: <c>execute_tool {gen_ai.tool.name}</c>.</summary>
    internal static Activity? StartTool(ToolCallPart call, Tool tool, string? agentName, string? conversationId)
    {
        if (!AiTelemetry.ActivitySource.HasListeners())
        {
            return null;
        }

        List<KeyValuePair<string, object?>> tags =
        [
            new(OperationNameKey, ExecuteTool),
            new("gen_ai.tool.name", call.ToolName),
            new("gen_ai.tool.type", "function"),
        ];
        if (!string.IsNullOrEmpty(agentName))
        {
            tags.Add(new("gen_ai.agent.name", agentName));
        }

        var activity = AiTelemetry.ActivitySource.StartActivity(SpanName(ExecuteTool, call.ToolName), ActivityKind.Internal, default(ActivityContext), tags);
        if (activity is { IsAllDataRequested: true })
        {
            SetTagIfNotEmpty(activity, "gen_ai.tool.call.id", call.ToolCallId);
            SetTagIfNotEmpty(activity, "gen_ai.tool.description", tool.Description);
            SetTagIfNotEmpty(activity, "gen_ai.conversation.id", conversationId);
        }

        return activity;
    }

    private static ClientOperation? StartClientOperation(string operation, string provider, string model)
    {
        var tracing = AiTelemetry.ActivitySource.HasListeners();
        if (!tracing && !OperationDuration.Enabled && !TokenUsage.Enabled)
        {
            return null;
        }

        var providerName = ProviderName(provider);
        Activity? activity = null;
        if (tracing)
        {
            KeyValuePair<string, object?>[] tags =
            [
                new(OperationNameKey, operation),
                new(ProviderNameKey, providerName),
                new(RequestModelKey, model),
            ];
            activity = AiTelemetry.ActivitySource.StartActivity(SpanName(operation, model), ActivityKind.Client, default(ActivityContext), tags);
        }

        return new ClientOperation(activity, operation, providerName, model);
    }

    private static string SpanName(string operation, string? target) =>
        string.IsNullOrEmpty(target) ? operation : $"{operation} {target}";

    private static void SetTagIfNotEmpty(Activity activity, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            activity.SetTag(key, value);
        }
    }

    private static void SetRequestTags(
        Activity activity,
        int? maxTokens,
        double? temperature,
        double? topP,
        int? topK,
        double? presencePenalty,
        double? frequencyPenalty,
        IReadOnlyList<string>? stopSequences,
        int? seed)
    {
        activity.SetTag("gen_ai.request.max_tokens", maxTokens);
        activity.SetTag("gen_ai.request.temperature", temperature);
        activity.SetTag("gen_ai.request.top_p", topP);
        activity.SetTag("gen_ai.request.top_k", topK);
        activity.SetTag("gen_ai.request.presence_penalty", presencePenalty);
        activity.SetTag("gen_ai.request.frequency_penalty", frequencyPenalty);
        activity.SetTag("gen_ai.request.seed", seed);
        if (stopSequences is { Count: > 0 })
        {
            activity.SetTag("gen_ai.request.stop_sequences", stopSequences.ToArray());
        }
    }

    private static void SetUsageTags(Activity activity, Usage usage, bool breakdown)
    {
        activity.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
        activity.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
        if (breakdown)
        {
            // Los desgloses solo se informan si el proveedor los da y no son cero.
            activity.SetTag("gen_ai.usage.cache_read.input_tokens", usage.CachedInputTokens is > 0 ? usage.CachedInputTokens : null);
            activity.SetTag("gen_ai.usage.reasoning.output_tokens", usage.ReasoningTokens is > 0 ? usage.ReasoningTokens : null);
        }
    }

    private static string GetVersion()
    {
        var version = typeof(GenAiTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(GenAiTelemetry).Assembly.GetName().Version?.ToString()
                      ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }

    /// <summary>
    /// A client call to a model (<c>chat</c> or <c>embeddings</c>): its span plus the <c>gen_ai.client.operation.duration</c>
    /// and <c>gen_ai.client.token.usage</c> measurements, recorded once when it completes or fails.
    /// </summary>
    internal sealed class ClientOperation(Activity? activity, string operation, string provider, string model) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _firstChunk;
        private bool _recorded;

        public Activity? Activity { get; } = activity;

        /// <summary>Records <c>gen_ai.response.time_to_first_chunk</c> (streaming) the first time it is called.</summary>
        public void FirstChunk()
        {
            if (_firstChunk)
            {
                return;
            }

            _firstChunk = true;
            Activity?.SetTag("gen_ai.response.time_to_first_chunk", Stopwatch.GetElapsedTime(_started).TotalSeconds);
        }

        public void Complete(LanguageModelResult result)
        {
            if (Activity is { IsAllDataRequested: true } a)
            {
                SetTagIfNotEmpty(a, "gen_ai.response.id", result.Response?.Id);
                SetTagIfNotEmpty(a, ResponseModelKey, result.Response?.ModelId);
                a.SetTag("gen_ai.response.finish_reasons", new[] { FinishReasonName(result.FinishReason) });
                SetUsageTags(a, result.Usage, breakdown: true);
            }

            Record(result.Response?.ModelId, null, result.Usage.InputTokens, result.Usage.OutputTokens);
        }

        public void Complete(EmbeddingModelResult result)
        {
            if (Activity is { IsAllDataRequested: true } a)
            {
                a.SetTag("gen_ai.usage.input_tokens", result.Usage?.Tokens);
            }

            Record(null, null, result.Usage?.Tokens, null);
        }

        public void Fail(Exception error)
        {
            SetError(Activity, error);
            Record(null, ErrorType(error), null, null);
        }

        public void Dispose()
        {
            Record(null, null, null, null);
            Activity?.Dispose();
        }

        private void Record(string? responseModel, string? errorType, int? inputTokens, int? outputTokens)
        {
            if (_recorded)
            {
                return;
            }

            _recorded = true;
            if (!OperationDuration.Enabled && !TokenUsage.Enabled)
            {
                return;
            }

            var tags = new TagList
            {
                { OperationNameKey, operation },
                { ProviderNameKey, provider },
            };
            if (!string.IsNullOrEmpty(model))
            {
                tags.Add(RequestModelKey, model);
            }

            if (!string.IsNullOrEmpty(responseModel))
            {
                tags.Add(ResponseModelKey, responseModel);
            }

            if (errorType is not null)
            {
                tags.Add(ErrorTypeKey, errorType);
            }

            OperationDuration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);

            // Solo se informan los tokens que el proveedor devolvió (nunca se estiman).
            if (inputTokens is { } input)
            {
                var inputTags = tags;
                inputTags.Add("gen_ai.token.type", "input");
                TokenUsage.Record(input, inputTags);
            }

            if (outputTokens is { } output)
            {
                var outputTags = tags;
                outputTags.Add("gen_ai.token.type", "output");
                TokenUsage.Record(output, outputTags);
            }
        }
    }
}
