namespace AiSdk;

public static class LanguageModelExtensions
{
    /// <summary>Convierte un resultado completo en un stream simulado (útil para modelos sin streaming nativo).</summary>
    public static async IAsyncEnumerable<StreamPart> ToStreamParts(this LanguageModelResult result)
    {
        if (result.Response is { } response)
        {
            yield return new ResponseMetadataPart(response);
        }

        var i = 0;
        foreach (var part in result.Content)
        {
            var id = (i++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (part)
            {
                case TextPart t:
                    yield return new TextDeltaPart(id, t.Text);
                    break;
                case ReasoningPart r:
                    yield return new ReasoningDeltaPart(id, r.Text);
                    yield return new ReasoningEndPart(id) { ProviderMetadata = r.ProviderMetadata };
                    break;
                case ToolCallPart c:
                    yield return new ToolInputStartPart(c.ToolCallId, c.ToolName);
                    yield return new ToolInputDeltaPart(c.ToolCallId, c.Input.GetRawText());
                    yield return new ToolCallStreamPart(c);
                    break;
            }
        }

        yield return new ModelFinishPart(result.FinishReason, result.Usage);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Envuelve el modelo con middleware.</summary>
    public static ILanguageModel WithMiddleware(this ILanguageModel model, params ILanguageModelMiddleware[] middleware) =>
        middleware.Reverse().Aggregate(model, (inner, m) => new WrappedLanguageModel(inner, m));
}
