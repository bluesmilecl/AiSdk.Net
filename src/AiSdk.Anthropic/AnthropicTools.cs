using System.Text.Json.Nodes;

namespace AiSdk.Anthropic;

/// <summary>
/// Herramientas de servidor de Anthropic: las ejecuta la API de Claude y sus resultados
/// llegan en la misma respuesta (equivalente a <c>anthropic.tools.*</c> del AI SDK).
/// </summary>
public static class AnthropicTools
{
    /// <summary>
    /// Búsqueda web (<c>web_search_20260209</c>, con filtrado dinámico). Los resultados
    /// aparecen como <see cref="SourcePart"/> en <c>result.Sources</c>.
    /// No combinar con <see cref="CodeExecution"/>: el filtrado dinámico ya ejecuta código internamente.
    /// </summary>
    /// <param name="maxUses">Máximo de búsquedas por petición.</param>
    /// <param name="allowedDomains">Solo buscar en estos dominios (no combinar con <paramref name="blockedDomains"/>).</param>
    /// <param name="blockedDomains">Excluir estos dominios.</param>
    /// <param name="userLocation">Ubicación aproximada para resultados locales, p. ej. <c>("Santiago", "CL")</c>.</param>
    /// <param name="version">Tipo de la herramienta. Usa <c>"web_search_20250305"</c> en modelos anteriores a Claude 4.6 o en Vertex AI.</param>
    public static Tool WebSearch(
        int? maxUses = null,
        IEnumerable<string>? allowedDomains = null,
        IEnumerable<string>? blockedDomains = null,
        (string City, string Country)? userLocation = null,
        string version = "web_search_20260209")
    {
        var definition = new JsonObject { ["type"] = version, ["name"] = "web_search" };
        AddCommon(definition, maxUses, allowedDomains, blockedDomains);
        if (userLocation is { } location)
        {
            definition["user_location"] = new JsonObject { ["type"] = "approximate", ["city"] = location.City, ["country"] = location.Country };
        }

        return Tool.ProviderDefined("anthropic", "web_search", definition);
    }

    /// <summary>Lectura de páginas web cuyas URL ya aparecen en la conversación (<c>web_fetch_20260209</c>).</summary>
    /// <param name="maxUses">Máximo de lecturas por petición.</param>
    /// <param name="allowedDomains">Solo leer estos dominios.</param>
    /// <param name="blockedDomains">No leer estos dominios.</param>
    /// <param name="citations">Habilita citas sobre el contenido leído.</param>
    /// <param name="maxContentTokens">Límite de tokens del contenido leído.</param>
    /// <param name="version">Tipo de la herramienta (<c>"web_fetch_20250910"</c> para modelos anteriores).</param>
    public static Tool WebFetch(
        int? maxUses = null,
        IEnumerable<string>? allowedDomains = null,
        IEnumerable<string>? blockedDomains = null,
        bool citations = false,
        int? maxContentTokens = null,
        string version = "web_fetch_20260209")
    {
        var definition = new JsonObject { ["type"] = version, ["name"] = "web_fetch" };
        AddCommon(definition, maxUses, allowedDomains, blockedDomains);
        if (citations)
        {
            definition["citations"] = new JsonObject { ["enabled"] = true };
        }

        if (maxContentTokens is { } max)
        {
            definition["max_content_tokens"] = max;
        }

        return Tool.ProviderDefined("anthropic", "web_fetch", definition);
    }

    /// <summary>Ejecución de código en un contenedor de Anthropic (<c>code_execution_20260521</c>).</summary>
    public static Tool CodeExecution(string version = "code_execution_20260521") =>
        Tool.ProviderDefined("anthropic", "code_execution", new JsonObject { ["type"] = version, ["name"] = "code_execution" });

    /// <summary>Cualquier otra herramienta de servidor por su tipo (para versiones nuevas o betas).</summary>
    public static Tool ServerTool(string type, string name, JsonObject? extra = null)
    {
        var definition = new JsonObject { ["type"] = type, ["name"] = name };
        Internal.JsonUtil.DeepMerge(definition, extra);
        return Tool.ProviderDefined("anthropic", name, definition);
    }

    private static void AddCommon(JsonObject definition, int? maxUses, IEnumerable<string>? allowed, IEnumerable<string>? blocked)
    {
        if (allowed is not null && blocked is not null)
        {
            throw new ArgumentException("Usa allowedDomains o blockedDomains, no ambos.");
        }

        if (maxUses is { } uses)
        {
            definition["max_uses"] = uses;
        }

        if (allowed is not null)
        {
            definition["allowed_domains"] = new JsonArray(allowed.Select(d => (JsonNode?)d).ToArray());
        }

        if (blocked is not null)
        {
            definition["blocked_domains"] = new JsonArray(blocked.Select(d => (JsonNode?)d).ToArray());
        }
    }
}
