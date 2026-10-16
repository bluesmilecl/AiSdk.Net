namespace AiSdk;

/// <summary>
/// Punto de entrada principal del SDK (equivalente a las funciones
/// <c>generateText</c>, <c>streamText</c>, <c>generateObject</c>, <c>streamObject</c>,
/// <c>embed</c> y <c>embedMany</c> del AI SDK).
/// </summary>
public static class Ai
{
    /// <summary>Genera texto, ejecutando herramientas en bucle según <see cref="GenerateTextOptions.StopWhen"/>.</summary>
    public static Task<GenerateTextResult> GenerateTextAsync(GenerateTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return GenerationEngine.GenerateAsync(options);
    }

    /// <summary>Atajo: genera texto a partir de un prompt.</summary>
    public static Task<GenerateTextResult> GenerateTextAsync(ILanguageModel model, string prompt, CancellationToken cancellationToken = default) =>
        GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = prompt, CancellationToken = cancellationToken });

    /// <summary>Genera texto en streaming. Devuelve inmediatamente.</summary>
    public static StreamTextResult StreamText(GenerateTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new StreamTextResult(options);
    }

    /// <summary>Atajo: streaming a partir de un prompt.</summary>
    public static StreamTextResult StreamText(ILanguageModel model, string prompt, CancellationToken cancellationToken = default) =>
        StreamText(new GenerateTextOptions { Model = model, Prompt = prompt, CancellationToken = cancellationToken });

    /// <summary>Genera un objeto tipado que cumple el esquema de <typeparamref name="T"/>.</summary>
    public static async Task<GenerateObjectResult<T>> GenerateObjectAsync<T>(GenerateObjectOptions options)
    {
        var output = Output.Object<T>(options.SchemaName, options.SchemaDescription);
        var result = await GenerateTextAsync(ToTextOptions(options, output)).ConfigureAwait(false);
        return new GenerateObjectResult<T>(result.GetOutput<T>(), result.FinishReason, result.TotalUsage, result.Response, result.Text);
    }

    /// <summary>Genera un objeto tipado en streaming (objetos parciales + objeto final).</summary>
    public static StreamObjectResult<T> StreamObject<T>(GenerateObjectOptions options)
    {
        var output = Output.Object<T>(options.SchemaName, options.SchemaDescription);
        return new StreamObjectResult<T>(StreamText(ToTextOptions(options, output)));
    }

    /// <summary>Genera un embedding para un valor.</summary>
    public static async Task<EmbedResult> EmbedAsync(IEmbeddingModel model, string value, CancellationToken cancellationToken = default)
    {
        var result = await model.EmbedAsync([value], cancellationToken).ConfigureAwait(false);
        return new EmbedResult(value, result.Embeddings[0], result.Usage);
    }

    /// <summary>Genera embeddings para varios valores, dividiendo en lotes si el modelo lo requiere.</summary>
    public static async Task<EmbedManyResult> EmbedManyAsync(IEmbeddingModel model, IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        var batchSize = model.MaxEmbeddingsPerCall ?? int.MaxValue;
        var embeddings = new List<float[]>(values.Count);
        var tokens = 0;
        foreach (var chunk in values.Chunk(Math.Max(1, batchSize)))
        {
            var result = await model.EmbedAsync(chunk, cancellationToken).ConfigureAwait(false);
            embeddings.AddRange(result.Embeddings);
            tokens += result.Usage?.Tokens ?? 0;
        }

        return new EmbedManyResult(values, embeddings, new EmbeddingUsage(tokens));
    }

    /// <summary>Similitud coseno entre dos vectores.</summary>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Los vectores deben tener la misma longitud.");
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static GenerateTextOptions ToTextOptions(GenerateObjectOptions o, Output output) => new()
    {
        Model = o.Model,
        System = o.System,
        Prompt = o.Prompt,
        Messages = o.Messages,
        Output = output,
        MaxOutputTokens = o.MaxOutputTokens,
        Temperature = o.Temperature,
        TopP = o.TopP,
        TopK = o.TopK,
        PresencePenalty = o.PresencePenalty,
        FrequencyPenalty = o.FrequencyPenalty,
        StopSequences = o.StopSequences,
        Seed = o.Seed,
        MaxRetries = o.MaxRetries,
        Headers = o.Headers,
        ProviderOptions = o.ProviderOptions,
        CancellationToken = o.CancellationToken,
    };
}
