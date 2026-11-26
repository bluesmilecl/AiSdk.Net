using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Internal;
using AiSdk.Resources;
using System.Globalization;

namespace AiSdk;

/// <summary>Context available while a tool is executing.</summary>
public sealed class ToolExecutionContext
{
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>Gets the messages sent to the model in the step that originated the call.</summary>
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    public int StepNumber { get; init; }

    /// <summary>Gets optional services (for example, the ASP.NET Core <see cref="IServiceProvider"/>).</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Gets arbitrary state supplied by the caller (<see cref="CallSettings.Context"/>).</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    internal Action<StreamPart>? Emit { get; init; }

    /// <summary>Gets the approval handler of the current call (sub-agents inherit it if they do not have their own).</summary>
    internal Func<ToolApprovalRequest, ValueTask<bool>>? OnToolApproval { get; init; }

    /// <summary>
    /// Sends custom data to the UI while the tool is executing (progress, intermediate
    /// results...). It reaches <c>useChat</c> as <c>data-{type}</c> parts. Only has an effect when streaming.
    /// </summary>
    public void WriteData(string type, object? data, string? id = null, bool transient = false) =>
        Emit?.Invoke(new DataStreamPart(type, data is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(data, data.GetType(), JsonSchema.DefaultSerializerOptions))
        {
            Id = id,
            Transient = transient,
        });
}

/// <summary>Low-level execution function of a tool.</summary>
public delegate ValueTask<object?> ToolExecuteFunc(JsonElement input, ToolExecutionContext context, CancellationToken cancellationToken);

/// <summary>
/// Tool that the model can invoke (equivalent to the AI SDK's <c>tool()</c>).
/// If <see cref="Execute"/> is null, the tool is a "client" tool: the agent loop
/// stops and returns the call so that the caller can resolve it.
/// </summary>
public sealed class Tool
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public ToolExecuteFunc? Execute { get; init; }

    /// <summary>
    /// Gets the input validation that runs before execution (returns the error message, or null if the input is valid).
    /// <see cref="Create{TInput,TOutput}(string,string,Func{TInput,ToolExecutionContext,CancellationToken,Task{TOutput}},JsonSerializerOptions?)"/>
    /// and <see cref="FromDelegate"/> configure it automatically.
    /// </summary>
    public Func<JsonElement, string?>? ValidateInput { get; init; }

    /// <summary>
    /// Gets a predicate that, if it returns true, makes execution require approval through
    /// <see cref="CallSettings.OnToolApproval"/> (human-in-the-loop). Without a handler, the call is not executed and
    /// the model receives an error, except with <see cref="CallSettings.DeferToolApprovals"/>.
    /// </summary>
    public Func<ToolCallPart, ValueTask<bool>>? NeedsApproval { get; init; }

    /// <summary>Gets whether strict schema validation is requested from the provider.</summary>
    public bool Strict { get; init; }

    public JsonSerializerOptions SerializerOptions { get; init; } = JsonSchema.DefaultSerializerOptions;

    /// <summary>Gets the provider that executes this tool on its server (null = local tool).</summary>
    public string? ProviderName { get; init; }

    /// <summary>Gets the raw definition sent to the provider (server-side tools only).</summary>
    public JsonObject? ProviderDefinition { get; init; }

    public bool IsProviderTool => ProviderDefinition is not null;

    public ToolDefinition ToDefinition() => new(Name, Description, InputSchema)
    {
        Strict = Strict,
        ProviderName = ProviderName,
        ProviderDefinition = ProviderDefinition,
    };

    /// <summary>
    /// Tool executed by the provider (web search, code execution...).
    /// Usually created with each provider's factories (<c>AnthropicTools</c>, <c>OpenAITools</c>).
    /// </summary>
    public static Tool ProviderDefined(string providerName, string name, JsonObject definition) => new()
    {
        Name = name,
        Description = null,
        InputSchema = JsonUtil.EmptyObject,
        ProviderName = providerName,
        ProviderDefinition = definition,
    };

    /// <summary>Marks the tool as subject to human approval.</summary>
    public Tool RequireApproval(Func<ToolCallPart, bool>? predicate = null) => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
        Execute = Execute,
        ValidateInput = ValidateInput,
        Strict = Strict,
        SerializerOptions = SerializerOptions,
        ProviderName = ProviderName,
        ProviderDefinition = ProviderDefinition,
        NeedsApproval = call => ValueTask.FromResult(predicate?.Invoke(call) ?? true),
    };

    // ------------------------------------------------------------------
    // Fábricas tipadas
    // ------------------------------------------------------------------

    /// <summary>Creates a tool whose input is <typeparamref name="TInput"/> (the schema is generated automatically).</summary>
    public static Tool Create<TInput, TOutput>(
        string name,
        string description,
        Func<TInput, ToolExecutionContext, CancellationToken, Task<TOutput>> execute,
        JsonSerializerOptions? serializerOptions = null)
    {
        var options = serializerOptions ?? JsonSchema.DefaultSerializerOptions;
        return new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonSchema.For<TInput>(options),
            SerializerOptions = options,
            ValidateInput = input =>
            {
                try
                {
                    return input.Deserialize<TInput>(options) is null ? Strings.Tool_InputIsNull : null;
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    return ex.Message;
                }
            },
            Execute = async (input, ctx, ct) =>
            {
                var typed = input.Deserialize<TInput>(options)
                    ?? throw new JsonException(string.Format(CultureInfo.CurrentCulture, Strings.Tool_ToolInputIsNull, name));
                return await execute(typed, ctx, ct).ConfigureAwait(false);
            },
        };
    }

    public static Tool Create<TInput, TOutput>(string name, string description, Func<TInput, CancellationToken, Task<TOutput>> execute) =>
        Create<TInput, TOutput>(name, description, (input, _, ct) => execute(input, ct));

    public static Tool Create<TInput, TOutput>(string name, string description, Func<TInput, Task<TOutput>> execute) =>
        Create<TInput, TOutput>(name, description, (input, _, _) => execute(input));

    public static Tool Create<TInput, TOutput>(string name, string description, Func<TInput, TOutput> execute) =>
        Create<TInput, TOutput>(name, description, (input, _, _) => Task.FromResult(execute(input)));

    /// <summary>Creates a tool without an execution function (it is resolved on the client).</summary>
    public static Tool Define<TInput>(string name, string description) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSchema.For<TInput>(),
    };

    /// <summary>Creates a tool with a hand-written JSON schema.</summary>
    public static Tool FromSchema(string name, string description, JsonElement inputSchema, ToolExecuteFunc? execute) => new()
    {
        Name = name,
        Description = description,
        InputSchema = inputSchema,
        Execute = execute,
    };

    /// <summary>
    /// Creates a tool from any delegate or method. Each parameter
    /// becomes a schema property; use <see cref="DescriptionAttribute"/>
    /// to document them. <see cref="CancellationToken"/>,
    /// <see cref="ToolExecutionContext"/> and <see cref="IServiceProvider"/> parameters are injected.
    /// </summary>
    public static Tool FromDelegate(string name, string description, Delegate function, JsonSerializerOptions? serializerOptions = null)
    {
        var options = serializerOptions ?? JsonSchema.DefaultSerializerOptions;
        var method = function.Method;
        var parameters = method.GetParameters();
        var props = new List<(string, JsonNode, bool)>();
        foreach (var p in parameters)
        {
            if (IsInjected(p.ParameterType))
            {
                continue;
            }

            var schema = JsonSchema.NodeFor(p.ParameterType, options);
            var paramDescription = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (paramDescription is not null && schema is JsonObject so)
            {
                so["description"] = paramDescription;
            }

            props.Add((PropertyName(p, options), schema, !p.HasDefaultValue));
        }

        object?[] BindArguments(JsonElement input, ToolExecutionContext? ctx, CancellationToken ct)
        {
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.ParameterType == typeof(CancellationToken))
                {
                    args[i] = ct;
                }
                else if (p.ParameterType == typeof(ToolExecutionContext))
                {
                    args[i] = ctx;
                }
                else if (p.ParameterType == typeof(IServiceProvider))
                {
                    args[i] = ctx?.Services;
                }
                else if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty(PropertyName(p, options), out var value))
                {
                    args[i] = value.Deserialize(p.ParameterType, options);
                }
                else if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                }
                else
                {
                    throw new JsonException(string.Format(CultureInfo.CurrentCulture, Strings.Tool_MissingRequiredParameter, PropertyName(p, options)));
                }
            }

            return args;
        }

        return new Tool
        {
            Name = name,
            Description = description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description,
            InputSchema = JsonSchema.Object(props),
            SerializerOptions = options,
            ValidateInput = input =>
            {
                try
                {
                    BindArguments(input, null, CancellationToken.None);
                    return null;
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    return ex.Message;
                }
            },
            Execute = async (input, ctx, ct) =>
            {
                var args = BindArguments(input, ctx, ct);
                object? result;
                try
                {
                    result = function.DynamicInvoke(args);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    throw ex.InnerException;
                }

                return await AwaitResult(result).ConfigureAwait(false);
            },
        };
    }

    private static bool IsInjected(Type t) =>
        t == typeof(CancellationToken) || t == typeof(ToolExecutionContext) || t == typeof(IServiceProvider);

    private static string PropertyName(ParameterInfo p, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(p.Name!) ?? p.Name!;

    private static async ValueTask<object?> AwaitResult(object? result)
    {
        switch (result)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                return taskType.IsGenericType && taskType.GetGenericArguments()[0].Name != "VoidTaskResult"
                    ? taskType.GetProperty("Result")!.GetValue(task)
                    : null;
            case ValueTask vt:
                await vt.ConfigureAwait(false);
                return null;
        }

        var type = result.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)type.GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(result, null)!;
            return await AwaitResult(asTask).ConfigureAwait(false);
        }

        return result;
    }

    internal static JsonElement SerializeOutput(object? output, JsonSerializerOptions options) => output switch
    {
        null => JsonSerializer.SerializeToElement<object?>(null),
        JsonElement e => e,
        JsonNode n => JsonSerializer.SerializeToElement(n),
        _ => JsonSerializer.SerializeToElement(output, output.GetType(), options),
    };

    internal static JsonElement ErrorOutput(string message) => JsonSerializer.SerializeToElement(message);

    internal static JsonElement EmptyInput => JsonUtil.EmptyObject;
}
