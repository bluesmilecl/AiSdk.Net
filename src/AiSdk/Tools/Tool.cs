using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Internal;

namespace AiSdk;

/// <summary>Contexto disponible durante la ejecución de una herramienta.</summary>
public sealed class ToolExecutionContext
{
    public required string ToolCallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>Mensajes enviados al modelo en el paso que originó la llamada.</summary>
    public required IReadOnlyList<ModelMessage> Messages { get; init; }

    public int StepNumber { get; init; }

    /// <summary>Servicios opcionales (por ejemplo, el <see cref="IServiceProvider"/> de ASP.NET Core).</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Estado arbitrario aportado por el llamador (<see cref="CallSettings.Context"/>).</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }
}

/// <summary>Función de ejecución de bajo nivel de una herramienta.</summary>
public delegate ValueTask<object?> ToolExecuteFunc(JsonElement input, ToolExecutionContext context, CancellationToken cancellationToken);

/// <summary>
/// Herramienta que el modelo puede invocar (equivalente a <c>tool()</c> del AI SDK).
/// Si <see cref="Execute"/> es null la herramienta es "de cliente": el bucle del agente
/// se detiene y devuelve la llamada para que la resuelva el llamador.
/// </summary>
public sealed class Tool
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public ToolExecuteFunc? Execute { get; init; }

    /// <summary>
    /// Si devuelve true, la ejecución requiere aprobación mediante
    /// <see cref="CallSettings.OnToolApproval"/> (human-in-the-loop).
    /// </summary>
    public Func<ToolCallPart, ValueTask<bool>>? NeedsApproval { get; init; }

    /// <summary>Solicita validación estricta del esquema al proveedor.</summary>
    public bool Strict { get; init; }

    public JsonSerializerOptions SerializerOptions { get; init; } = JsonSchema.DefaultSerializerOptions;

    public ToolDefinition ToDefinition() => new(Name, Description, InputSchema) { Strict = Strict };

    /// <summary>Marca la herramienta como sujeta a aprobación humana.</summary>
    public Tool RequireApproval(Func<ToolCallPart, bool>? predicate = null) => new()
    {
        Name = Name,
        Description = Description,
        InputSchema = InputSchema,
        Execute = Execute,
        Strict = Strict,
        SerializerOptions = SerializerOptions,
        NeedsApproval = call => ValueTask.FromResult(predicate?.Invoke(call) ?? true),
    };

    // ------------------------------------------------------------------
    // Fábricas tipadas
    // ------------------------------------------------------------------

    /// <summary>Crea una herramienta cuyo input es <typeparamref name="TInput"/> (el esquema se genera automáticamente).</summary>
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
            Execute = async (input, ctx, ct) =>
            {
                var typed = input.Deserialize<TInput>(options)
                    ?? throw new JsonException($"La entrada de la herramienta '{name}' es nula.");
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

    /// <summary>Herramienta sin función de ejecución (se resuelve en el cliente).</summary>
    public static Tool Define<TInput>(string name, string description) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSchema.For<TInput>(),
    };

    /// <summary>Herramienta con esquema JSON escrito a mano.</summary>
    public static Tool FromSchema(string name, string description, JsonElement inputSchema, ToolExecuteFunc? execute) => new()
    {
        Name = name,
        Description = description,
        InputSchema = inputSchema,
        Execute = execute,
    };

    /// <summary>
    /// Crea una herramienta a partir de cualquier delegado o método. Cada parámetro se
    /// convierte en una propiedad del esquema; usa <see cref="DescriptionAttribute"/>
    /// para documentarlos. Los parámetros <see cref="CancellationToken"/>,
    /// <see cref="ToolExecutionContext"/> e <see cref="IServiceProvider"/> se inyectan.
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

        return new Tool
        {
            Name = name,
            Description = description ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description,
            InputSchema = JsonSchema.Object(props),
            SerializerOptions = options,
            Execute = async (input, ctx, ct) =>
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
                        args[i] = ctx.Services;
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
                        throw new JsonException($"Falta el parámetro requerido '{p.Name}'.");
                    }
                }

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
