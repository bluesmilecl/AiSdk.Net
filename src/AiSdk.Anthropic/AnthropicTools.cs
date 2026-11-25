using System.Text.Json.Nodes;

namespace AiSdk.Anthropic;

/// <summary>
/// Anthropic server tools: executed by the Claude API, with their results
/// arriving in the same response (equivalent to the AI SDK's <c>anthropic.tools.*</c>).
/// </summary>
public static class AnthropicTools
{
    /// <summary>
    /// Web search (<c>web_search_20260209</c>, with dynamic filtering). The results
    /// appear as <see cref="SourcePart"/> in <c>result.Sources</c>.
    /// Do not combine with <see cref="CodeExecution"/>: dynamic filtering already executes code internally.
    /// </summary>
    /// <param name="maxUses">Maximum number of searches per request.</param>
    /// <param name="allowedDomains">Only search these domains (do not combine with <paramref name="blockedDomains"/>).</param>
    /// <param name="blockedDomains">Exclude these domains.</param>
    /// <param name="userLocation">Approximate location for local results, e.g. <c>("Santiago", "CL")</c>.</param>
    /// <param name="version">Tool type. Use <c>"web_search_20250305"</c> for models earlier than Claude 4.6 or on Vertex AI.</param>
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

    /// <summary>Fetches web pages whose URLs already appear in the conversation (<c>web_fetch_20260209</c>).</summary>
    /// <param name="maxUses">Maximum number of fetches per request.</param>
    /// <param name="allowedDomains">Only fetch these domains.</param>
    /// <param name="blockedDomains">Do not fetch these domains.</param>
    /// <param name="citations">Enables citations on the fetched content.</param>
    /// <param name="maxContentTokens">Token limit for the fetched content.</param>
    /// <param name="version">Tool type (<c>"web_fetch_20250910"</c> for earlier models).</param>
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

    /// <summary>Code execution in an Anthropic container (<c>code_execution_20260521</c>).</summary>
    public static Tool CodeExecution(string version = "code_execution_20260521") =>
        Tool.ProviderDefined("anthropic", "code_execution", new JsonObject { ["type"] = version, ["name"] = "code_execution" });

    /// <summary>Any other server tool by its type (for new or beta versions).</summary>
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
