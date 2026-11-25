using System.Text.Json.Nodes;

namespace AiSdk.OpenAI;

/// <summary>
/// OpenAI server tools (Responses API only: <c>openai.Responses("gpt-5")</c>).
/// </summary>
public static class OpenAITools
{
    /// <summary>Web search. Citations appear as <see cref="SourcePart"/> in <c>result.Sources</c>.</summary>
    /// <param name="searchContextSize"><c>low</c>, <c>medium</c> or <c>high</c>.</param>
    /// <param name="allowedDomains">Restricts the search to these domains.</param>
    /// <param name="userLocation">Approximate location, e.g. <c>("Santiago", "CL")</c>.</param>
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

    /// <summary>Code interpreter in a container managed by OpenAI.</summary>
    public static Tool CodeInterpreter(string? containerId = null) =>
        Tool.ProviderDefined("openai", "code_interpreter", new JsonObject
        {
            ["type"] = "code_interpreter",
            ["container"] = containerId is null ? new JsonObject { ["type"] = "auto" } : containerId,
        });

    /// <summary>Search in OpenAI vector stores.</summary>
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

    /// <summary>Any other server tool by its type.</summary>
    public static Tool ServerTool(string type, string name, JsonObject? extra = null)
    {
        var definition = new JsonObject { ["type"] = type };
        Internal.JsonUtil.DeepMerge(definition, extra);
        return Tool.ProviderDefined("openai", name, definition);
    }
}

/// <summary>Typed builder of <see cref="ProviderOptions"/> for OpenAI.</summary>
public static class OpenAIOptions
{
    /// <param name="reasoningEffort"><c>minimal</c>, <c>low</c>, <c>medium</c> or <c>high</c> (reasoning models).</param>
    /// <param name="reasoningSummary">
    /// <c>auto</c> or <c>detailed</c> to receive the reasoning summary (Responses API only).
    /// </param>
    /// <param name="store">
    /// false to not store the response at OpenAI (Responses API only); in that case the encrypted
    /// reasoning is requested so it can be forwarded in subsequent turns.
    /// </param>
    /// <param name="parallelToolCalls">Allows multiple tool calls in the same step.</param>
    /// <param name="serviceTier"><c>auto</c>, <c>default</c>, <c>flex</c> or <c>priority</c>.</param>
    /// <param name="user">Stable identifier of the end user (for abuse detection).</param>
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
