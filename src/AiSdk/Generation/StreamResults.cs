using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiSdk.Internal;

namespace AiSdk;

/// <summary>
/// Resultado de <see cref="Ai.StreamText(GenerateTextOptions)"/>. La generación comienza de inmediato; los
/// streams pueden consumirse varias veces y por varios consumidores a la vez.
/// </summary>
public sealed class StreamTextResult
{
    private readonly ReplayBuffer<StreamPart> _buffer = new();
    private readonly TaskCompletionSource<GenerateTextResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StreamTextResult(GenerateTextOptions options)
    {
        Options = options;
        _ = Task.Run(() => GenerationEngine.RunStreamAsync(options, _buffer, _completion));
        // Evita excepciones no observadas si nadie espera el resultado.
        _completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    internal GenerateTextOptions Options { get; }

    /// <summary>Todas las partes: texto, razonamiento, llamadas y resultados de herramientas, pasos, errores.</summary>
    public IAsyncEnumerable<StreamPart> FullStream => _buffer.ReadAllAsync();

    /// <summary>Solo los fragmentos de texto. Lanza la excepción si el stream falla.</summary>
    public IAsyncEnumerable<string> TextStream => ReadText();

    /// <summary>Resultado completo, disponible al terminar.</summary>
    public Task<GenerateTextResult> Result => _completion.Task;

    public Task<string> Text => Then(r => r.Text);

    public Task<IReadOnlyList<StepResult>> Steps => Then(r => r.Steps);

    public Task<Usage> TotalUsage => Then(r => r.TotalUsage);

    public Task<FinishReason> FinishReason => Then(r => r.FinishReason);

    public Task<IReadOnlyList<ModelMessage>> ResponseMessages => Then(r => r.ResponseMessages);

    /// <summary>Consume el stream completo sin procesarlo (útil si solo interesan los callbacks).</summary>
    public async Task ConsumeAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <summary>
    /// Stream de la salida estructurada parcial (requiere <see cref="GenerateTextOptions.Output"/>).
    /// Emite un nuevo valor cada vez que el JSON parcial cambia.
    /// </summary>
    public async IAsyncEnumerable<T> PartialOutputStream<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = Options.Output as Output<T> ?? throw new InvalidOperationException($"Output no es de tipo {typeof(T).Name}.");
        var text = new StringBuilder();
        string? last = null;
        await foreach (var part in _buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (part)
            {
                case StartStepPart:
                    text.Clear();
                    break;
                case TextDeltaPart d:
                    text.Append(d.Delta);
                    var partial = output.ParsePartial(text.ToString());
                    if (partial is null)
                    {
                        break;
                    }

                    var serialized = JsonSerializer.Serialize(partial, JsonSchema.DefaultSerializerOptions);
                    if (serialized != last)
                    {
                        last = serialized;
                        yield return partial;
                    }

                    break;
                case ErrorPart e:
                    throw e.Error;
            }
        }
    }

    private async IAsyncEnumerable<string> ReadText([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in _buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (part)
            {
                case TextDeltaPart d:
                    yield return d.Delta;
                    break;
                case ErrorPart e:
                    throw e.Error;
            }
        }
    }

    private async Task<TResult> Then<TResult>(Func<GenerateTextResult, TResult> selector) =>
        selector(await _completion.Task.ConfigureAwait(false));
}

/// <summary>Resultado de <see cref="Ai.StreamObject{T}"/>.</summary>
public sealed class StreamObjectResult<T>
{
    private readonly StreamTextResult _inner;

    internal StreamObjectResult(StreamTextResult inner) => _inner = inner;

    /// <summary>Objetos parciales a medida que llega el JSON.</summary>
    public IAsyncEnumerable<T> PartialObjectStream => _inner.PartialOutputStream<T>();

    /// <summary>JSON crudo a medida que llega.</summary>
    public IAsyncEnumerable<string> TextStream => _inner.TextStream;

    public IAsyncEnumerable<StreamPart> FullStream => _inner.FullStream;

    /// <summary>Objeto final validado.</summary>
    public Task<T> Object => GetObjectAsync();

    public Task<Usage> Usage => _inner.TotalUsage;

    private async Task<T> GetObjectAsync() => (await _inner.Result.ConfigureAwait(false)).GetOutput<T>();
}
