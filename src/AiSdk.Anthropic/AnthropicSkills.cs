using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiSdk.Http;
using AiSdk.Skills;

namespace AiSdk.Anthropic;

/// <summary>Skill que se carga en el contenedor de ejecución de código de Claude.</summary>
/// <param name="Type"><c>anthropic</c> (pptx, xlsx, docx, pdf) o <c>custom</c> (subida con <see cref="AnthropicSkillsClient"/>).</param>
/// <param name="SkillId">Id: <c>pptx</c>, <c>xlsx</c>... o <c>skill_01...</c>.</param>
/// <param name="Version"><c>latest</c> o un número de versión concreto.</param>
public sealed record AnthropicSkill(string Type, string SkillId, string Version = "latest")
{
    /// <summary>Presentaciones PowerPoint.</summary>
    public static AnthropicSkill PowerPoint { get; } = new("anthropic", "pptx");

    /// <summary>Hojas de cálculo Excel (con fórmulas y gráficos).</summary>
    public static AnthropicSkill Excel { get; } = new("anthropic", "xlsx");

    /// <summary>Documentos Word.</summary>
    public static AnthropicSkill Word { get; } = new("anthropic", "docx");

    /// <summary>Lectura, llenado y creación de PDF.</summary>
    public static AnthropicSkill Pdf { get; } = new("anthropic", "pdf");

    /// <summary>Skill propia subida a la API.</summary>
    public static AnthropicSkill Custom(string skillId, string version = "latest") => new("custom", skillId, version);

    internal JsonObject ToJson() => new() { ["type"] = Type, ["skill_id"] = SkillId, ["version"] = Version };
}

/// <summary>
/// Skills ejecutadas por Claude en su contenedor (Agent Skills de la API de Anthropic). A diferencia de las
/// skills locales (<see cref="SkillLibrary"/>), corren en la nube de Anthropic con acceso a código y archivos,
/// y producen archivos descargables (por ejemplo, un .pptx).
/// </summary>
/// <example>
/// <code>
/// var result = await Ai.GenerateTextAsync(new()
/// {
///     Model = anthropic.Messages("claude-opus-5-5"),
///     Prompt = "Crea una presentación de 5 diapositivas sobre nuestros resultados",
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
    /// Opciones del proveedor que cargan las skills en el contenedor (agregue también
    /// <see cref="AnthropicTools.CodeExecution"/> a las herramientas). El contenedor se reutiliza
    /// automáticamente en los turnos siguientes de la misma conversación.
    /// </summary>
    /// <param name="skills">Hasta 8 skills por petición.</param>
    /// <param name="containerId">Contenedor existente a reutilizar (opcional).</param>
    /// <param name="baseOptions">Opciones que se combinan (p. ej. <c>AnthropicOptions.Create(effort: "high")</c>).</param>
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

    /// <summary>Ids de los archivos generados en el contenedor durante la respuesta (descárguelos con <see cref="AnthropicFileClient.DownloadAsync"/>).</summary>
    public static IReadOnlyList<string> GetGeneratedFileIds(GenerateTextResult result) =>
        result.Steps.SelectMany(s => s.Content).OfType<ToolResultPart>()
            .Where(r => r.ProviderExecuted)
            .SelectMany(r => FindFileIds(r.Output))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Id del contenedor usado en la conversación (para reutilizarlo explícitamente).</summary>
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

/// <summary>Skill registrada en la API de Anthropic.</summary>
public sealed record AnthropicSkillInfo(string Id, string? DisplayTitle, string Source, string? LatestVersion, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Versión de una skill en la API de Anthropic.</summary>
public sealed record AnthropicSkillVersion(string SkillId, string Version, string? Name, string? Description, DateTimeOffset? CreatedAt);

/// <summary>
/// API de Skills de Anthropic (<c>/v1/skills</c>): sube skills propias (creadas con <see cref="SkillBuilder"/>,
/// cargadas de una carpeta o generadas por un agente) para usarlas con <see cref="AnthropicSkill.Custom"/>.
/// </summary>
public sealed class AnthropicSkillsClient
{
    private readonly AnthropicProvider _provider;

    internal AnthropicSkillsClient(AnthropicProvider provider) => _provider = provider;

    private IEnumerable<KeyValuePair<string, string>> Headers => _provider.Headers(null, [AnthropicSkills.SkillsBeta]);

    /// <summary>Sube una skill nueva (todos sus archivos) y devuelve su id.</summary>
    public async Task<AnthropicSkillInfo> CreateAsync(Skill skill, string? displayTitle = null, CancellationToken cancellationToken = default)
    {
        var request = ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url("skills"), Headers);
        request.Content = await FormAsync(skill, displayTitle ?? skill.Name, cancellationToken).ConfigureAwait(false);
        return ParseSkill(await ProviderHttp.SendJsonAsync(_provider.Http, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Publica una versión nueva de una skill existente.</summary>
    public async Task<AnthropicSkillVersion> CreateVersionAsync(string skillId, Skill skill, CancellationToken cancellationToken = default)
    {
        var request = ProviderHttp.CreateRequest(HttpMethod.Post, _provider.Url($"skills/{Uri.EscapeDataString(skillId)}/versions"), Headers);
        request.Content = await FormAsync(skill, null, cancellationToken).ConfigureAwait(false);
        return ParseVersion(await ProviderHttp.SendJsonAsync(_provider.Http, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lista skills (<paramref name="source"/>: <c>custom</c>, <c>anthropic</c> o null para todas).</summary>
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

    /// <summary>Elimina una skill (primero elimina todas sus versiones, como exige la API).</summary>
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
