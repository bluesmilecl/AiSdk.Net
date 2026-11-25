using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSdk.Artifacts;

/// <summary>Artifact types (the same ones Claude uses).</summary>
public static class ArtifactTypes
{
    /// <summary>Self-contained HTML page (inline CSS and JS).</summary>
    public const string Html = "html";

    public const string Markdown = "markdown";

    /// <summary>Source code (set <see cref="Artifact.Language"/>).</summary>
    public const string Code = "code";

    public const string Svg = "svg";

    /// <summary>Mermaid diagram.</summary>
    public const string Mermaid = "mermaid";

    /// <summary>React component (default export) that can be rendered in the browser.</summary>
    public const string React = "react";

    public const string Json = "json";

    public const string Csv = "csv";

    /// <summary>Generated binary file (Word, Excel, PDF, image...).</summary>
    public const string File = "file";

    public static readonly IReadOnlyList<string> Text = [Html, Markdown, Code, Svg, Mermaid, React, Json, Csv];

    public static string MediaTypeOf(string type) => type switch
    {
        Html => "text/html",
        Markdown => "text/markdown",
        Svg => "image/svg+xml",
        Json => "application/json",
        Csv => "text/csv",
        _ => "text/plain",
    };
}

/// <summary>A version of an artifact.</summary>
public sealed record Artifact
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Gets the type: one of <see cref="ArtifactTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the language of code artifacts (e.g. <c>"csharp"</c>, <c>"python"</c>).</summary>
    public string? Language { get; init; }

    /// <summary>Gets the text content (empty for binary files).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets the binary content (artifacts of type <see cref="ArtifactTypes.File"/>).</summary>
    public byte[]? Data { get; init; }

    public string? MediaType { get; init; }

    public string? FileName { get; init; }

    public int Version { get; init; } = 1;

    /// <summary>Gets the conversation the artifact belongs to (tools cannot see artifacts from other conversations).</summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Gets the user who owns the artifact. It is set by <see cref="ArtifactTools"/> from
    /// <see cref="ToolExecutionContext.Context"/> (key <see cref="ArtifactToolOptions.OwnerKey"/>), never by the model
    /// or the client; endpoints use it for authorization.
    /// </summary>
    public string? Owner { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonIgnore]
    public string EffectiveMediaType => MediaType ?? ArtifactTypes.MediaTypeOf(Type);
}

/// <summary>Artifact store with version history.</summary>
public interface IArtifactStore
{
    /// <summary>Saves a new version (the store assigns the version number).</summary>
    Task<Artifact> SaveAsync(Artifact artifact, CancellationToken cancellationToken = default);

    /// <summary>Gets the latest version, or the specified one.</summary>
    Task<Artifact?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> GetVersionsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the latest version of each artifact (optionally of one conversation). Intended for listings: stores may omit
    /// the binary content (<see cref="Artifact.Data"/>); use <see cref="GetAsync"/> to read a complete artifact.
    /// </summary>
    Task<IReadOnlyList<Artifact>> ListAsync(string? conversationId = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>In-memory store (development and testing).</summary>
public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<string, List<Artifact>> _items = new();

    public Task<Artifact> SaveAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        var versions = _items.GetOrAdd(artifact.Id, _ => []);
        lock (versions)
        {
            var saved = artifact with { Version = versions.Count + 1, CreatedAt = DateTimeOffset.UtcNow };
            versions.Add(saved);
            return Task.FromResult(saved);
        }
    }

    public Task<Artifact?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
    {
        if (!_items.TryGetValue(id, out var versions))
        {
            return Task.FromResult<Artifact?>(null);
        }

        lock (versions)
        {
            return Task.FromResult(version is null ? versions.LastOrDefault() : versions.FirstOrDefault(v => v.Version == version));
        }
    }

    public Task<IReadOnlyList<Artifact>> GetVersionsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_items.TryGetValue(id, out var versions))
        {
            return Task.FromResult<IReadOnlyList<Artifact>>([]);
        }

        lock (versions)
        {
            return Task.FromResult<IReadOnlyList<Artifact>>(versions.ToList());
        }
    }

    public Task<IReadOnlyList<Artifact>> ListAsync(string? conversationId = null, CancellationToken cancellationToken = default)
    {
        var latest = _items.Values
            .Select(v => { lock (v) { return v.Last(); } })
            .Where(a => conversationId is null || a.ConversationId == conversationId)
            .OrderBy(a => a.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Artifact>>(latest);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// File system store: one folder per artifact with one JSON file per version (and the binary stored separately).
/// Useful for small servers or for inspecting artifacts by hand.
/// </summary>
public sealed class FileSystemArtifactStore(string directory) : IArtifactStore, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Dispose() => _lock.Dispose();

    private string Folder(string id)
    {
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new ArgumentException($"Id de artifact inválido: '{id}'.", nameof(id));
        }

        return Path.Combine(directory, id);
    }

    public async Task<Artifact> SaveAsync(Artifact artifact, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Directory.CreateDirectory(Folder(artifact.Id)).FullName;
            var version = Directory.GetFiles(folder, "v*.json").Length + 1;
            var saved = artifact with { Version = version, CreatedAt = DateTimeOffset.UtcNow };
            if (saved.Data is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, $"v{version}.bin"), saved.Data, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Combine(folder, $"v{version}.json"), JsonSerializer.Serialize(saved with { Data = null }, JsonSchema.DefaultSerializerOptions), cancellationToken).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Artifact?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
    {
        var folder = Folder(id);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        version ??= Directory.GetFiles(folder, "v*.json").Length;
        return await ReadAsync(folder, version.Value, includeData: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Artifact>> GetVersionsAsync(string id, CancellationToken cancellationToken = default)
    {
        var folder = Folder(id);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var count = Directory.GetFiles(folder, "v*.json").Length;
        var list = new List<Artifact>();
        for (var v = 1; v <= count; v++)
        {
            if (await ReadAsync(folder, v, includeData: true, cancellationToken).ConfigureAwait(false) is { } artifact)
            {
                list.Add(artifact);
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<Artifact>> ListAsync(string? conversationId = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        // Solo metadatos: los binarios (.bin) no se leen para listar.
        var list = new List<Artifact>();
        foreach (var folder in Directory.GetDirectories(directory))
        {
            var version = Directory.GetFiles(folder, "v*.json").Length;
            if (version > 0
                && await ReadAsync(folder, version, includeData: false, cancellationToken).ConfigureAwait(false) is { } artifact
                && (conversationId is null || artifact.ConversationId == conversationId))
            {
                list.Add(artifact);
            }
        }

        return list.OrderBy(a => a.CreatedAt).ToList();
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var folder = Folder(id);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task<Artifact?> ReadAsync(string folder, int version, bool includeData, CancellationToken cancellationToken)
    {
        var json = Path.Combine(folder, $"v{version}.json");
        if (!File.Exists(json))
        {
            return null;
        }

        var artifact = JsonSerializer.Deserialize<Artifact>(await File.ReadAllTextAsync(json, cancellationToken).ConfigureAwait(false), JsonSchema.DefaultSerializerOptions)!;
        var bin = Path.Combine(folder, $"v{version}.bin");
        return includeData && File.Exists(bin) ? artifact with { Data = await File.ReadAllBytesAsync(bin, cancellationToken).ConfigureAwait(false) } : artifact;
    }
}

/// <summary>Options for <see cref="ArtifactTools"/>.</summary>
public sealed class ArtifactToolOptions
{
    /// <summary>Gets or sets the allowed types (by default, all text types).</summary>
    public IReadOnlyList<string> AllowedTypes { get; init; } = ArtifactTypes.Text;

    /// <summary>Gets or sets the maximum content size, in characters.</summary>
    public int MaxContentLength { get; init; } = 500_000;

    /// <summary>
    /// Gets or sets the public URL of an artifact (e.g. <c>a => $"/artifacts/{a.Id}"</c>), which is returned to the
    /// model and the UI.
    /// </summary>
    public Func<Artifact, string?>? Url { get; init; }

    /// <summary>
    /// Gets or sets the <see cref="ToolExecutionContext.Context"/> key that holds the conversation id.
    /// </summary>
    public string ConversationIdKey { get; init; } = "conversationId";

    /// <summary>
    /// Gets or sets the <see cref="ToolExecutionContext.Context"/> key that holds the owning user (the same one scheduled
    /// tasks use). It must come from server-side authentication, not from the client.
    /// </summary>
    public string OwnerKey { get; init; } = "userId";

    /// <summary>
    /// Gets or sets whether the tools can be used without a conversation or user in the context. Defaults to
    /// <c>false</c>: without a scope the tools fail, so that artifacts from different users are never mixed. Artifacts
    /// created this way have no owner and are only visible to other unscoped contexts. Only for single-user
    /// applications, scripts or tests.
    /// </summary>
    public bool AllowUnscoped { get; init; }

    /// <summary>
    /// Gets or sets whether to include the full content in the <c>data-artifact</c> data part sent to the UI.
    /// </summary>
    public bool SendContentToUI { get; init; } = true;

    /// <summary>Gets or sets an additional validation that runs before saving (throw an exception to reject).</summary>
    public Action<Artifact>? Validate { get; init; }
}

/// <summary>
/// Scope from which artifacts are accessed: the conversation and the user of the execution context.
/// </summary>
/// <param name="ConversationId">Current conversation (isolates artifacts between conversations).</param>
/// <param name="Owner">Authenticated user (isolates artifacts between users).</param>
public sealed record ArtifactScope(string? ConversationId, string? Owner)
{
    /// <summary>Gets whether the scope has neither a conversation nor a user.</summary>
    public bool IsEmpty => ConversationId is null && Owner is null;

    /// <summary>Reads the scope from <see cref="ToolExecutionContext.Context"/> using the keys in <paramref name="options"/>.</summary>
    public static ArtifactScope FromContext(ToolExecutionContext? context, ArtifactToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ArtifactScope(Read(context, options.ConversationIdKey), Read(context, options.OwnerKey));
    }

    /// <summary>
    /// Indicates whether this scope can view or edit <paramref name="artifact"/>:
    /// <list type="bullet">
    /// <item>if the artifact or the scope has a user, both must be the same;</item>
    /// <item>if both have a conversation, it must be the same;</item>
    /// <item>without users, an artifact of a conversation is only visible from that conversation, and one with neither a
    /// conversation nor a user only from an empty scope.</item>
    /// </list>
    /// </summary>
    public bool CanAccess(Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if ((Owner is not null || artifact.Owner is not null) && !string.Equals(artifact.Owner, Owner, StringComparison.Ordinal))
        {
            return false;
        }

        if (artifact.ConversationId is not null && ConversationId is not null && !string.Equals(artifact.ConversationId, ConversationId, StringComparison.Ordinal))
        {
            return false;
        }

        if (Owner is not null)
        {
            return true;
        }

        return artifact.ConversationId is not null ? string.Equals(artifact.ConversationId, ConversationId, StringComparison.Ordinal) : IsEmpty;
    }

    private static string? Read(ToolExecutionContext? context, string key) =>
        context?.Context is not null && context.Context.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;
}

/// <summary>
/// Tools that let an agent create and edit artifacts like Claude does: substantial, self-contained content
/// (HTML pages, documents, code, diagrams, React components) that is shown apart from the chat, versioned,
/// and editable with exact replacements.
/// </summary>
/// <remarks>
/// Each change is sent to the UI as a <c>data-artifact</c> data part (reconciled by id), so <c>useChat</c> can show it
/// in a side panel. The tools require a conversation or a user in <see cref="ToolExecutionContext.Context"/> (see
/// <see cref="ArtifactScope"/>) unless <see cref="ArtifactToolOptions.AllowUnscoped"/> is set.
/// </remarks>
public static class ArtifactTools
{
    /// <summary>Recommended system instructions for agents that create artifacts.</summary>
    public const string Instructions =
        """
        Puedes crear artifacts: contenido sustancial y autocontenido que el usuario querrá ver, reutilizar o modificar
        (páginas o apps HTML, documentos Markdown largos, código de más de ~15 líneas, diagramas SVG o Mermaid,
        componentes React). No uses artifacts para respuestas cortas, explicaciones o fragmentos breves.
        - Usa create_artifact una vez por pieza; para cambios pequeños usa update_artifact con un reemplazo exacto,
          y rewrite_artifact solo si cambia la mayor parte.
        - HTML: un único archivo con CSS y JS en línea; diseño responsive y accesible.
        - React: un componente con export default, sin props obligatorias, con Tailwind para estilos.
        - Tras crear o editar, resume en una o dos frases qué hiciste; no repitas el contenido en el chat.
        """;

    public static IReadOnlyList<Tool> Create(IArtifactStore store, ArtifactToolOptions? options = null)
    {
        options ??= new ArtifactToolOptions();
        var o = options;

        ArtifactScope Scope(ToolExecutionContext ctx) => RequireScope(ctx, o);

        async Task<object> SaveAsync(Artifact artifact, string action, ToolExecutionContext ctx, CancellationToken ct)
        {
            if (artifact.Content.Length > o.MaxContentLength)
            {
                throw new ArgumentException($"El contenido supera el máximo de {o.MaxContentLength} caracteres.");
            }

            o.Validate?.Invoke(artifact);
            var saved = await store.SaveAsync(artifact, ct).ConfigureAwait(false);
            var url = o.Url?.Invoke(saved);
            ctx.WriteData("artifact", new
            {
                action,
                id = saved.Id,
                title = saved.Title,
                type = saved.Type,
                language = saved.Language,
                version = saved.Version,
                url,
                content = o.SendContentToUI ? saved.Content : null,
            }, id: saved.Id);
            return new { id = saved.Id, version = saved.Version, url, status = action };
        }

        // Un artifact de otra conversación o de otro usuario se trata igual que uno inexistente.
        static Artifact Visible(Artifact? artifact, ArtifactScope scope, string id) =>
            artifact is not null && scope.CanAccess(artifact)
                ? artifact
                : throw new UnauthorizedAccessException($"No existe el artifact '{id}' en esta conversación.");

        async Task<Artifact> LoadAsync(string id, ArtifactScope scope, CancellationToken ct) =>
            Visible(await store.GetAsync(id, cancellationToken: ct).ConfigureAwait(false), scope, id);

        var allowed = string.Join(", ", o.AllowedTypes);
        return
        [
            Tool.Create<CreateInput, object>(
                "create_artifact",
                $"Crea un artifact nuevo. Tipos: {allowed}.",
                async (input, ctx, ct) =>
                {
                    var scope = Scope(ctx);
                    if (!o.AllowedTypes.Contains(input.Type))
                    {
                        throw new ArgumentException($"Tipo no permitido: '{input.Type}'. Use uno de: {allowed}.");
                    }

                    var id = string.IsNullOrWhiteSpace(input.Id) ? Slug(input.Title) : Slug(input.Id);
                    if (await store.GetAsync(id, cancellationToken: ct).ConfigureAwait(false) is not null)
                    {
                        id = $"{id}-{Guid.NewGuid().ToString("N")[..6]}";
                    }

                    return await SaveAsync(
                        new Artifact { Id = id, Title = input.Title, Type = input.Type, Language = input.Language, Content = input.Content, ConversationId = scope.ConversationId, Owner = scope.Owner },
                        "created",
                        ctx,
                        ct).ConfigureAwait(false);
                }),
            Tool.Create<UpdateInput, object>(
                "update_artifact",
                "Edita un artifact reemplazando un fragmento exacto (oldStr debe aparecer una sola vez). Úsalo para cambios pequeños.",
                async (input, ctx, ct) =>
                {
                    var current = await LoadAsync(input.Id, Scope(ctx), ct).ConfigureAwait(false);
                    var first = current.Content.IndexOf(input.OldStr, StringComparison.Ordinal);
                    if (input.OldStr.Length == 0 || first < 0)
                    {
                        throw new ArgumentException("oldStr no aparece en el artifact. Copia el texto exacto (incluidos espacios) o usa rewrite_artifact.");
                    }

                    if (current.Content.IndexOf(input.OldStr, first + 1, StringComparison.Ordinal) >= 0)
                    {
                        throw new ArgumentException("oldStr aparece más de una vez; incluye más contexto para que sea único.");
                    }

                    var content = string.Concat(current.Content.AsSpan(0, first), input.NewStr, current.Content.AsSpan(first + input.OldStr.Length));
                    return await SaveAsync(current with { Content = content }, "updated", ctx, ct).ConfigureAwait(false);
                }),
            Tool.Create<RewriteInput, object>(
                "rewrite_artifact",
                "Reemplaza todo el contenido de un artifact existente (nueva versión).",
                async (input, ctx, ct) =>
                {
                    var current = await LoadAsync(input.Id, Scope(ctx), ct).ConfigureAwait(false);
                    return await SaveAsync(current with { Content = input.Content, Title = input.Title ?? current.Title }, "rewritten", ctx, ct).ConfigureAwait(false);
                }),
            Tool.Create<ReadInput, object>(
                "read_artifact",
                "Lee el contenido actual (o una versión) de un artifact.",
                async (input, ctx, ct) =>
                {
                    var scope = Scope(ctx);
                    var current = await LoadAsync(input.Id, scope, ct).ConfigureAwait(false);
                    if (input.Version is { } v)
                    {
                        current = Visible(
                            await store.GetAsync(input.Id, v, ct).ConfigureAwait(false) ?? throw new ArgumentException($"No existe la versión {v}."),
                            scope,
                            input.Id);
                    }

                    return new { id = current.Id, title = current.Title, type = current.Type, version = current.Version, content = current.Data is null ? current.Content : $"[archivo binario {current.FileName}, {current.Data.Length} bytes]" };
                }),
            Tool.Create<ListInput, object>(
                "list_artifacts",
                "Lista los artifacts de la conversación.",
                async (_, ctx, ct) =>
                {
                    var scope = Scope(ctx);
                    return (await store.ListAsync(scope.ConversationId, ct).ConfigureAwait(false))
                        .Where(scope.CanAccess)
                        .Select(a => new { id = a.Id, title = a.Title, type = a.Type, version = a.Version, url = o.Url?.Invoke(a) })
                        .ToList();
                }),
        ];
    }

    /// <summary>
    /// Saves a generated binary file (Word, Excel, PDF...) as an artifact and announces it to the UI.
    /// With <paramref name="context"/> (a call from a tool) the file belongs to that conversation and user, and
    /// without a scope it fails just like the tools; without a context (application code) it has no owner.
    /// </summary>
    public static async Task<Artifact> SaveFileAsync(
        IArtifactStore store,
        byte[] data,
        string fileName,
        string mediaType,
        string? title,
        ToolExecutionContext? context,
        ArtifactToolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);
        options ??= new ArtifactToolOptions();
        var scope = context is null ? new ArtifactScope(null, null) : RequireScope(context, options);
        var id = Slug(Path.GetFileNameWithoutExtension(fileName));
        if (await store.GetAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false) is { } existing
            && (existing.ConversationId != scope.ConversationId || existing.Owner != scope.Owner))
        {
            id = $"{id}-{Guid.NewGuid().ToString("N")[..6]}";
        }

        var saved = await store.SaveAsync(new Artifact
        {
            Id = id,
            Title = title ?? fileName,
            Type = ArtifactTypes.File,
            Data = data,
            MediaType = mediaType,
            FileName = fileName,
            ConversationId = scope.ConversationId,
            Owner = scope.Owner,
        }, cancellationToken).ConfigureAwait(false);
        context?.WriteData("artifact", new
        {
            action = "file",
            id = saved.Id,
            title = saved.Title,
            type = saved.Type,
            fileName,
            mediaType,
            size = data.Length,
            version = saved.Version,
            url = options.Url?.Invoke(saved),
        }, id: saved.Id);
        return saved;
    }

    private static ArtifactScope RequireScope(ToolExecutionContext context, ArtifactToolOptions options)
    {
        var scope = ArtifactScope.FromContext(context, options);
        return scope.IsEmpty && !options.AllowUnscoped
            ? throw new UnauthorizedAccessException(
                $"Los artifacts necesitan el id de conversación ('{options.ConversationIdKey}') o de usuario ('{options.OwnerKey}') en el contexto de ejecución.")
            : scope;
    }

    /// <summary>Creates a readable, URL-safe id from a title.</summary>
    public static string Slug(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if ((c is ' ' or '-' or '_' or '.') && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? Guid.NewGuid().ToString("N")[..12] : slug[..Math.Min(slug.Length, 60)];
    }

    private sealed record CreateInput(
        [property: Description("Título breve.")] string Title,
        [property: Description("Tipo: html, markdown, code, svg, mermaid, react, json o csv.")] string Type,
        [property: Description("Contenido completo.")] string Content,
        [property: Description("Lenguaje si type es code (csharp, python, sql...).")] string? Language = null,
        [property: Description("Id opcional en kebab-case.")] string? Id = null);

    private sealed record UpdateInput(
        [property: Description("Id del artifact.")] string Id,
        [property: Description("Texto exacto a reemplazar (único).")] string OldStr,
        [property: Description("Texto nuevo.")] string NewStr);

    private sealed record RewriteInput(
        [property: Description("Id del artifact.")] string Id,
        [property: Description("Contenido completo nuevo.")] string Content,
        [property: Description("Título nuevo (opcional).")] string? Title = null);

    private sealed record ReadInput(
        [property: Description("Id del artifact.")] string Id,
        [property: Description("Versión (opcional; por defecto la última).")] int? Version = null);

    private sealed record ListInput;
}
