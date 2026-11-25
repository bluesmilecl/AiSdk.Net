using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Skills;

namespace AiSdk.Anthropic;

/// <summary>Skill loaded into Claude's code execution container.</summary>
/// <param name="Type"><c>anthropic</c> (pptx, xlsx, docx, pdf) or <c>custom</c> (uploaded with <see cref="AnthropicSkillsClient"/>).</param>
/// <param name="SkillId">Id: <c>pptx</c>, <c>xlsx</c>... or <c>skill_01...</c>.</param>
/// <param name="Version"><c>latest</c> or a specific version number.</param>
public sealed record AnthropicSkill(string Type, string SkillId, string Version = "latest")
{
    /// <summary>PowerPoint presentations.</summary>
    public static AnthropicSkill PowerPoint { get; } = new("anthropic", "pptx");

    /// <summary>Excel spreadsheets (with formulas and charts).</summary>
    public static AnthropicSkill Excel { get; } = new("anthropic", "xlsx");

    /// <summary>Word documents.</summary>
    public static AnthropicSkill Word { get; } = new("anthropic", "docx");

    /// <summary>Reading, filling and creating PDFs.</summary>
    public static AnthropicSkill Pdf { get; } = new("anthropic", "pdf");

    /// <summary>Custom skill uploaded to the API.</summary>
    public static AnthropicSkill Custom(string skillId, string version = "latest") => new("custom", skillId, version);

    internal JsonObject ToJson() => new() { ["type"] = Type, ["skill_id"] = SkillId, ["version"] = Version };
}

/// <summary>
/// Skills executed by Claude in its container (Anthropic API Agent Skills). Unlike
/// local skills (<see cref="SkillLibrary"/>), they run in Anthropic's cloud with access to code and files,
/// and produce downloadable files (for example, a .pptx).
/// </summary>
/// <example>
/// <code>
/// var result = await Ai.GenerateTextAsync(new()
/// {
///     Model = anthropic.Messages("claude-opus-5-5"),
///     Prompt = "Create a 5-slide presentation about our results",
///     Tools = [AnthropicTools.CodeExecution()],
///     ProviderOptions = AnthropicSkills.Options([AnthropicSkill.PowerPoint]),
/// });
/// foreach (var fileId in AnthropicSkills.GetGeneratedFileIds(result))
///     File.WriteAllBytes($"{fileId}.pptx", await anthropic.Files.DownloadAsync(fileId));
/// </code>
/// </example>
public static class AnthropicSkills
{
    public const string SkillsBeta = "skills-2025-10-02";
    public const string CodeExecutionBeta = "code-execution-2025-08-25";

    /// <summary>
    /// Provider options that load the skills into the container (also add
    /// <see cref="AnthropicTools.CodeExecution"/> to the tools). The container is reused
    /// automatically in subsequent turns of the same conversation.
    /// </summary>
    /// <param name="skills">Up to 8 skills per request.</param>
    /// <param name="containerId">Existing container to reuse (optional).</param>
    /// <param name="baseOptions">Options to merge with (e.g. <c>AnthropicOptions.Create(effort: "high")</c>).</param>
    public static ProviderOptions Options(IEnumerable<AnthropicSkill> skills, string? containerId = null, ProviderOptions? baseOptions = null)
    {
        var list = skills.ToList();
        if (list.Count > 8)
        {
            throw new ArgumentException("La API admite hasta 8 skills por petición.", nameof(skills));
        }

        var anthropic = baseOptions?.For("anthropic")?.DeepClone() as JsonObject ?? [];
        var container = new JsonObject { ["skills"] = new JsonArray(list.Select(s => (JsonNode)s.ToJson()).ToArray()) };
        if (containerId is not null)
        {
            container["id"] = containerId;
        }

        anthropic["container"] = container;
        var betas = anthropic["betas"] as JsonArray ?? [];
        foreach (var beta in new[] { SkillsBeta, CodeExecutionBeta, AnthropicFileClient.Beta })
        {
            if (!betas.Any(b => (string?)b == beta))
            {
                betas.Add(beta);
            }
        }

        anthropic["betas"] = betas;
        var options = new ProviderOptions();
        foreach (var (provider, value) in baseOptions ?? new ProviderOptions())
        {
            options[provider] = value;
        }

        options["anthropic"] = anthropic;
        return options;
    }

    /// <summary>Ids of the files generated in the container during the response (download them with <see cref="AnthropicFileClient.DownloadAsync"/>).</summary>
    public static IReadOnlyList<string> GetGeneratedFileIds(GenerateTextResult result) =>
        result.Steps.SelectMany(s => s.Content).OfType<ToolResultPart>()
            .Where(r => r.ProviderExecuted)
            .SelectMany(r => FindFileIds(r.Output))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Id of the container used in the conversation (to reuse it explicitly).</summary>
    public static string? GetContainerId(IEnumerable<ModelMessage> messages) =>
        AnthropicMessagesLanguageModel.FindContainerId(messages);

    private static IEnumerable<string> FindFileIds(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "file_id" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString()!;
                    }
                    else
                    {
                        foreach (var id in FindFileIds(property.Value)) yield return id;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var id in FindFileIds(item)) yield return id;
                }

                break;
        }
    }
}

/// <summary>Skill registered in the Anthropic API.</summary>
public sealed record AnthropicSkillInfo(string Id, string? DisplayTitle, string Source, string? LatestVersion, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Version of a skill in the Anthropic API.</summary>
public sealed record AnthropicSkillVersion(string SkillId, string Version, string? Name, string? Description, DateTimeOffset? CreatedAt);

/// <summary>
/// Anthropic Skills API (<c>/v1/skills</c>): uploads custom skills (created with <see cref="SkillBuilder"/>,
/// loaded from a folder or generated by an agent) for use with <see cref="AnthropicSkill.Custom"/>.
/// </summary>
public sealed class AnthropicSkillsClient
{
    private readonly AnthropicProvider _provider;

    internal AnthropicSkillsClient(AnthropicProvider provider) => _provider = provider;

    private IEnumerable<KeyValuePair<string, string>> Headers => _provider.Headers(null, [AnthropicSkills.SkillsBeta]);

    /// <summary>Uploads a new skill (all its files) and returns its id.</summary>
    public async Task<AnthropicSkillInfo> CreateAsync(Skill skill, string? displayTitle = null, CancellationToken cancellationToken = default)
    {
        var request = ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url("skills"), Headers);
        request.Content = await FormAsync(skill, displayTitle ?? skill.Name, cancellationToken).ConfigureAwait(false);
        return ParseSkill(await ProviderHttp.SendJsonAsync(_provider.Http, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Publishes a new version of an existing skill.</summary>
    public async Task<AnthropicSkillVersion> CreateVersionAsync(string skillId, Skill skill, CancellationToken cancellationToken = default)
    {
        var request = ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url($"skills/{Uri.EscapeDataString(skillId)}/versions"), Headers);
        request.Content = await FormAsync(skill, null, cancellationToken).ConfigureAwait(false);
        return ParseVersion(await ProviderHttp.SendJsonAsync(_provider.Http, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists skills (<paramref name="source"/>: <c>custom</c>, <c>anthropic</c> or null for all).</summary>
    public async Task<IReadOnlyList<AnthropicSkillInfo>> ListAsync(string? source = null, CancellationToken cancellationToken = default)
    {
        var query = source is null ? string.Empty : $"?source={Uri.EscapeDataString(source)}";
        var json = await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"skills{query}"), Headers), cancellationToken).ConfigureAwait(false);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(ParseSkill).ToList();
    }

    public async Task<AnthropicSkillInfo> GetAsync(string skillId, CancellationToken cancellationToken = default) =>
        ParseSkill(await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"skills/{Uri.EscapeDataString(skillId)}"), Headers), cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<AnthropicSkillVersion>> ListVersionsAsync(string skillId, CancellationToken cancellationToken = default)
    {
        var json = await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Get, _provider.Url($"skills/{Uri.EscapeDataString(skillId)}/versions"), Headers), cancellationToken).ConfigureAwait(false);
        return (json["data"] as JsonArray ?? []).OfType<JsonObject>().Select(ParseVersion).ToList();
    }

    /// <summary>Deletes a skill (first deleting all its versions, as the API requires).</summary>
    public async Task DeleteAsync(string skillId, CancellationToken cancellationToken = default)
    {
        foreach (var version in await ListVersionsAsync(skillId, cancellationToken).ConfigureAwait(false))
        {
            await ProviderHttp.SendJsonAsync(
                _provider.Http,
                ProviderHttp.CreateRequest(HttpMethod.Delete, _provider.Url($"skills/{Uri.EscapeDataString(skillId)}/versions/{Uri.EscapeDataString(version.Version)}"), Headers),
                cancellationToken).ConfigureAwait(false);
        }

        await ProviderHttp.SendJsonAsync(_provider.Http, ProviderHttp.CreateRequest(HttpMethod.Delete, _provider.Url($"skills/{Uri.EscapeDataString(skillId)}"), Headers), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MultipartFormDataContent> FormAsync(Skill skill, string? displayTitle, CancellationToken cancellationToken)
    {
        skill.Validate().ThrowIfInvalid(skill.Name);
        var form = new MultipartFormDataContent();
        if (displayTitle is not null)
        {
            form.Add(new StringContent(displayTitle, Encoding.UTF8), "display_title");
        }

        // Todos los archivos deben estar bajo una carpeta raíz con SKILL.md.
        AddFile(form, $"{skill.Name}/SKILL.md", Encoding.UTF8.GetBytes(skill.ToMarkdown()));
        foreach (var path in skill.Files.List())
        {
            if (await skill.Files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } data)
            {
                AddFile(form, $"{skill.Name}/{path}", data);
            }
        }

        return form;
    }

    private static void AddFile(MultipartFormDataContent form, string path, byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".md", StringComparison.Ordinal) ? "text/markdown" : "application/octet-stream");
        form.Add(content, "files[]", path);
    }

    private static AnthropicSkillInfo ParseSkill(JsonObject json) => new(
        (string?)json["id"] ?? string.Empty,
        (string?)json["display_title"],
        (string?)json["source"] ?? "custom",
        json["latest_version"]?.ToString(),
        ProviderHttp.ParseTimestamp(json["created_at"]),
        ProviderHttp.ParseTimestamp(json["updated_at"]));

    private static AnthropicSkillVersion ParseVersion(JsonObject json) => new(
        (string?)json["skill_id"] ?? string.Empty,
        json["version"]?.ToString() ?? string.Empty,
        (string?)json["name"],
        (string?)json["description"],
        ProviderHttp.ParseTimestamp(json["created_at"]));
}
