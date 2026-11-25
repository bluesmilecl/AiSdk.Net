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
        var result = await GenerateTextAsync(ToTextOptions(options, output, "generate_object")).ConfigureAwait(false);
        return new GenerateObjectResult<T>(result.GetOutput<T>(), result.FinishReason, result.TotalUsage, result.Response, result.Text);
    }

    /// <summary>Genera un objeto tipado en streaming (objetos parciales + objeto final).</summary>
    public static StreamObjectResult<T> StreamObject<T>(GenerateObjectOptions options)
    {
        var output = Output.Object<T>(options.SchemaName, options.SchemaDescription);
        return new StreamObjectResult<T>(StreamText(ToTextOptions(options, output, "stream_object")));
    }

    /// <summary>Genera un embedding para un valor.</summary>
    public static async Task<EmbedResult> EmbedAsync(IEmbeddingModel model, string value, CancellationToken cancellationToken = default)
    {
        var result = await EmbedCallAsync(model, [value], cancellationToken).ConfigureAwait(false);
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
            var result = await EmbedCallAsync(model, chunk, cancellationToken).ConfigureAwait(false);
            embeddings.AddRange(result.Embeddings);
            tokens += result.Usage?.Tokens ?? 0;
        }

        return new EmbedManyResult(values, embeddings, new EmbeddingUsage(tokens));
    }

    /// <summary>
    /// Genera imágenes. Si se piden más de las que admite el modelo por llamada,
    /// se hacen varias llamadas en paralelo.
    /// </summary>
    public static async Task<GenerateImageResult> GenerateImageAsync(GenerateImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var perCall = Math.Max(1, options.Model.MaxImagesPerCall ?? options.N);
        var batches = new List<int>();
        for (var remaining = options.N; remaining > 0; remaining -= perCall)
        {
            batches.Add(Math.Min(perCall, remaining));
        }

        var results = await Task.WhenAll(batches.Select(n => Internal.Retry.ExecuteAsync(
            ct => options.Model.GenerateAsync(new ImageModelRequest
            {
                Prompt = options.Prompt,
                N = n,
                Size = options.Size,
                AspectRatio = options.AspectRatio,
                Seed = options.Seed,
                ProviderOptions = options.ProviderOptions,
                Headers = options.Headers,
            }, ct),
            options.MaxRetries,
            options.CancellationToken))).ConfigureAwait(false);

        var images = results.SelectMany(r => r.Images).ToList();
        if (images.Count == 0)
        {
            throw new AiSdkException("El modelo no generó ninguna imagen.");
        }

        return new GenerateImageResult(images, results.SelectMany(r => r.Warnings).ToList());
    }

    /// <summary>Convierte texto en audio.</summary>
    public static async Task<GenerateSpeechResult> GenerateSpeechAsync(GenerateSpeechOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = await Internal.Retry.ExecuteAsync(
            ct => options.Model.GenerateAsync(new SpeechModelRequest
            {
                Text = options.Text,
                Voice = options.Voice,
                OutputFormat = options.OutputFormat,
                Instructions = options.Instructions,
                Speed = options.Speed,
                Language = options.Language,
                ProviderOptions = options.ProviderOptions,
                Headers = options.Headers,
            }, ct),
            options.MaxRetries,
            options.CancellationToken).ConfigureAwait(false);

        if (result.Audio.Data.Length == 0)
        {
            throw new AiSdkException("El modelo no generó audio.");
        }

        return new GenerateSpeechResult(result.Audio, result.Warnings);
    }

    /// <summary>Transcribe audio a texto.</summary>
    public static async Task<TranscriptionModelResult> TranscribeAsync(TranscribeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await Internal.Retry.ExecuteAsync(
            ct => options.Model.TranscribeAsync(new TranscriptionModelRequest
            {
                Audio = options.Audio,
                MediaType = options.MediaType,
                Filename = options.Filename,
                Language = options.Language,
                Prompt = options.Prompt,
                ProviderOptions = options.ProviderOptions,
                Headers = options.Headers,
            }, ct),
            options.MaxRetries,
            options.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reordena documentos de texto por relevancia respecto a la consulta.</summary>
    public static Task<RerankResult<string>> RerankAsync(
        IRerankingModel model,
        string query,
        IReadOnlyList<string> documents,
        int? topN = null,
        CancellationToken cancellationToken = default) =>
        RerankAsync(model, query, documents, d => d, topN, cancellationToken);

    /// <summary>Reordena objetos por relevancia, usando <paramref name="text"/> para obtener su texto.</summary>
    public static async Task<RerankResult<T>> RerankAsync<T>(
        IRerankingModel model,
        string query,
        IReadOnlyList<T> documents,
        Func<T, string> text,
        int? topN = null,
        CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
        {
            return new RerankResult<T>([]);
        }

        var result = await model.RerankAsync(
            new RerankModelRequest(query, documents.Select(text).ToList()) { TopN = topN },
            cancellationToken).ConfigureAwait(false);
        return new RerankResult<T>(result.Ranking
            .Where(r => r.Index >= 0 && r.Index < documents.Count)
            .Select(r => new RankedDocument<T>(documents[r.Index], r.Index, r.Score))
            .ToList());
    }

    /// <summary>
    /// Cuenta los tokens de entrada que consumiría la llamada (mensajes, sistema y herramientas).
    /// Requiere un modelo que implemente <see cref="ITokenCountingModel"/>.
    /// </summary>
    public static async Task<int> CountTokensAsync(GenerateTextOptions options)
    {
        var plan = await GenerationEngine.PrepareAsync(options, GenerationEngine.InitialMessages(options), []).ConfigureAwait(false);
        return plan.Model is ITokenCountingModel counter
            ? await counter.CountTokensAsync(plan.Request, options.CancellationToken).ConfigureAwait(false)
            : throw new NotSupportedException($"El modelo '{plan.Model.Provider}:{plan.Model.ModelId}' no permite contar tokens.");
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

    private static async Task<EmbeddingModelResult> EmbedCallAsync(IEmbeddingModel model, IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        using var operation = Internal.GenAiTelemetry.StartEmbeddings(model);
        try
        {
            var result = await model.EmbedAsync(values, cancellationToken).ConfigureAwait(false);
            operation?.Complete(result);
            return result;
        }
        catch (Exception ex)
        {
            operation?.Fail(ex);
            throw;
        }
    }

    private static GenerateTextOptions ToTextOptions(GenerateObjectOptions o, Output output, string entrypoint) => new()
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
        MaxRetryAfter = o.MaxRetryAfter,
        MaxParallelToolCalls = o.MaxParallelToolCalls,
        Headers = o.Headers,
        ProviderOptions = o.ProviderOptions,
        OnError = o.OnError,
        OnAbort = o.OnAbort,
        CancellationToken = o.CancellationToken,
        TelemetryEntrypoint = entrypoint,
    };
}
