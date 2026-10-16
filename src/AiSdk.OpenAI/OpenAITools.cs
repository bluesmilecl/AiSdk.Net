using System.Text.Json.Nodes;

namespace AiSdk.OpenAI;

/// <summary>
/// Herramientas de servidor de OpenAI (solo con la API Responses: <c>openai.Responses("gpt-5")</c>).
/// </summary>
public static class OpenAITools
{
    /// <summary>Búsqueda web. Las citas aparecen como <see cref="SourcePart"/> en <c>result.Sources</c>.</summary>
    /// <param name="searchContextSize"><c>low</c>, <c>medium</c> o <c>high</c>.</param>
    /// <param name="allowedDomains">Limita la búsqueda a estos dominios.</param>
    /// <param name="userLocation">Ubicación aproximada, p. ej. <c>("Santiago", "CL")</c>.</param>
    public static Tool WebSearch(
        string? searchContextSize = null,
        IEnumerable<string>? allowedDomains = null,
        (string City, string Country)? userLocation = null)
    {
        var definition = new JsonObject { ["type"] = "web_search" };
        if (searchContextSize is not null)
        {
            definition["search_context_size"] = searchContextSize;
        }

        if (allowedDomains is not null)
        {
            definition["filters"] = new JsonObject { ["allowed_domains"] = new JsonArray(allowedDomains.Select(d => (JsonNode?)d).ToArray()) };
        }

        if (userLocation is { } location)
        {
            definition["user_location"] = new JsonObject { ["type"] = "approximate", ["city"] = location.City, ["country"] = location.Country };
        }

        return Tool.ProviderDefined("openai", "web_search", definition);
    }

    /// <summary>Intérprete de código en un contenedor gestionado por OpenAI.</summary>
    public static Tool CodeInterpreter(string? containerId = null) =>
        Tool.ProviderDefined("openai", "code_interpreter", new JsonObject
        {
            ["type"] = "code_interpreter",
            ["container"] = containerId is null ? new JsonObject { ["type"] = "auto" } : containerId,
        });

    /// <summary>Búsqueda en vector stores de OpenAI.</summary>
    public static Tool FileSearch(IEnumerable<string> vectorStoreIds, int? maxNumResults = null)
    {
        var definition = new JsonObject
        {
            ["type"] = "file_search",
            ["vector_store_ids"] = new JsonArray(vectorStoreIds.Select(id => (JsonNode?)id).ToArray()),
        };
        if (maxNumResults is { } max)
        {
            definition["max_num_results"] = max;
        }

        return Tool.ProviderDefined("openai", "file_search", definition);
    }

    /// <summary>Cualquier otra herramienta de servidor por su tipo.</summary>
    public static Tool ServerTool(string type, string name, JsonObject? extra = null)
    {
        var definition = new JsonObject { ["type"] = type };
        Internal.JsonUtil.DeepMerge(definition, extra);
        return Tool.ProviderDefined("openai", name, definition);
    }
}

/// <summary>Constructor tipado de <see cref="ProviderOptions"/> para OpenAI.</summary>
public static class OpenAIOptions
{
    /// <param name="reasoningEffort"><c>minimal</c>, <c>low</c>, <c>medium</c> o <c>high</c> (modelos de razonamiento).</param>
    /// <param name="reasoningSummary">
    /// <c>auto</c> o <c>detailed</c> para recibir el resumen del razonamiento (solo API Responses).
    /// </param>
    /// <param name="store">
    /// false para no guardar la respuesta en OpenAI (solo API Responses); en ese caso se pide el
    /// razonamiento cifrado para poder reenviarlo en los siguientes turnos.
    /// </param>
    /// <param name="parallelToolCalls">Permite varias llamadas a herramientas en un mismo paso.</param>
    /// <param name="serviceTier"><c>auto</c>, <c>default</c>, <c>flex</c> o <c>priority</c>.</param>
    /// <param name="user">Identificador estable del usuario final (para detección de abuso).</param>
    public static ProviderOptions Create(
        string? reasoningEffort = null,
        string? reasoningSummary = null,
        bool? store = null,
        bool? parallelToolCalls = null,
        string? serviceTier = null,
        string? user = null)
    {
        var obj = new JsonObject();
        if (reasoningEffort is not null || reasoningSummary is not null)
        {
            var reasoning = new JsonObject();
            if (reasoningEffort is not null)
            {
                reasoning["effort"] = reasoningEffort;
            }

            if (reasoningSummary is not null)
            {
                reasoning["summary"] = reasoningSummary;
            }

            obj["reasoning"] = reasoning;
        }

        if (store is { } s)
        {
            obj["store"] = s;
            if (!s)
            {
                obj["include"] = new JsonArray("reasoning.encrypted_content");
            }
        }

        if (parallelToolCalls is { } p)
        {
            obj["parallel_tool_calls"] = p;
        }

        if (serviceTier is not null)
        {
            obj["service_tier"] = serviceTier;
        }

        if (user is not null)
        {
            obj["user"] = user;
        }

        return new ProviderOptions { ["openai"] = obj };
    }
}
