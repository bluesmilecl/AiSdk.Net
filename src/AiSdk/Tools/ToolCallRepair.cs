using System.Text.Json;
using AiSdk.Internal;

namespace AiSdk;

/// <summary>Tool call repair strategies (equivalent to <c>experimental_repairToolCall</c>).</summary>
public static class ToolCallRepair
{
    /// <summary>
    /// Asks a model (usually a fast, cheap one) to correct the tool's input
    /// according to its schema. Nonexistent tools are not repaired.
    /// </summary>
    public static Func<ToolCallRepairContext, ValueTask<ToolCallPart?>> WithModel(ILanguageModel model, int maxRetries = 1) =>
        async context =>
        {
            if (context.Tool is not { } tool || tool.IsProviderTool)
            {
                return null;
            }

            var prompt =
                $"""
                 El modelo intentó llamar a la herramienta "{tool.Name}" con una entrada inválida.
                 Error: {context.Error}

                 Entrada original:
                 {(context.ToolCall.Input.ValueKind == JsonValueKind.String ? context.ToolCall.Input.GetString() : context.ToolCall.Input.GetRawText())}

                 Esquema JSON de la entrada:
                 {tool.InputSchema.GetRawText()}

                 Devuelve únicamente la entrada corregida como un objeto JSON que cumpla el esquema,
                 conservando la intención original.
                 """;

            var result = await Retry.ExecuteAsync(
                ct => model.GenerateAsync(new LanguageModelRequest
                {
                    Messages = [ModelMessage.User(prompt)],
                    ResponseFormat = ResponseFormat.Json(tool.InputSchema, "tool_input"),
                }, ct),
                maxRetries,
                context.CancellationToken).ConfigureAwait(false);

            var text = string.Concat(result.Content.OfType<TextPart>().Select(t => t.Text)).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                text = text.Trim('`').Trim();
                if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[4..];
                }
            }

            return JsonUtil.TryParseElement(text, out var input) && input.ValueKind == JsonValueKind.Object
                ? context.ToolCall with { Input = input, InvalidInput = false }
                : null;
        };

    /// <summary>Repairs calls to tools whose name is misspelled (ignoring case, hyphens or spaces).</summary>
    public static Func<ToolCallRepairContext, ValueTask<ToolCallPart?>> FixToolNameCase() =>
        context =>
        {
            if (context.Reason != ToolCallRepairReason.UnknownTool)
            {
                return ValueTask.FromResult<ToolCallPart?>(null);
            }

            static string Normalize(string s) => s.Replace("-", "_", StringComparison.Ordinal).Replace(" ", "_", StringComparison.Ordinal).ToLowerInvariant();
            var match = context.Tools.FirstOrDefault(t => Normalize(t.Name) == Normalize(context.ToolCall.ToolName));
            return ValueTask.FromResult(match is null ? null : context.ToolCall with { ToolName = match.Name });
        };

    /// <summary>Combines strategies: the first one that returns a repaired call is used.</summary>
    public static Func<ToolCallRepairContext, ValueTask<ToolCallPart?>> FirstOf(params Func<ToolCallRepairContext, ValueTask<ToolCallPart?>>[] strategies) =>
        async context =>
        {
            foreach (var strategy in strategies)
            {
                if (await strategy(context).ConfigureAwait(false) is { } repaired)
                {
                    return repaired;
                }
            }

            return null;
        };
}
