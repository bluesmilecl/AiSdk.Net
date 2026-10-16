using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSdk.Artifacts;

/// <summary>Tipos de artifact (los mismos que usa Claude).</summary>
public static class ArtifactTypes
{
    /// <summary>Página HTML autocontenida (CSS y JS en línea).</summary>
    public const string Html = "html";

    public const string Markdown = "markdown";

    /// <summary>Código fuente (indicar <see cref="Artifact.Language"/>).</summary>
    public const string Code = "code";

    public const string Svg = "svg";

    /// <summary>Diagrama Mermaid.</summary>
    public const string Mermaid = "mermaid";

    /// <summary>Componente React (export default) renderizable en el navegador.</summary>
    public const string React = "react";

    public const string Json = "json";

    public const string Csv = "csv";

    /// <summary>Archivo binario generado (Word, Excel, PDF, imagen...).</summary>
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

/// <summary>Una versión de un artifact.</summary>
public sealed record Artifact
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Uno de <see cref="ArtifactTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Lenguaje para artifacts de código (p. ej. <c>"csharp"</c>, <c>"python"</c>).</summary>
    public string? Language { get; init; }

    /// <summary>Contenido de texto (vacío para archivos binarios).</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Contenido binario (artifacts de tipo <see cref="ArtifactTypes.File"/>).</summary>
    public byte[]? Data { get; init; }

    public string? MediaType { get; init; }

    public string? FileName { get; init; }

    public int Version { get; init; } = 1;

    /// <summary>Conversación o usuario dueño del artifact.</summary>
    public string? ConversationId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonIgnore]
    public string EffectiveMediaType => MediaType ?? ArtifactTypes.MediaTypeOf(Type);
}

/// <summary>Almacén de artifacts con historial de versiones.</summary>
public interface IArtifactStore
{
    /// <summary>Guarda una versión nueva (el número de versión lo asigna el almacén).</summary>
    Task<Artifact> SaveAsync(Artifact artifact, CancellationToken cancellationToken = default);

    /// <summary>Última versión, o la indicada.</summary>
    Task<Artifact?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> GetVersionsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Última versión de cada artifact (opcionalmente de una conversación).</summary>
    Task<IReadOnlyList<Artifact>> ListAsync(string? conversationId = null, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Almacén en memoria (desarrollo y pruebas).</summary>
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
/// Almacén en disco: una carpeta por artifact con un JSON por versión (y el binario aparte).
/// Útil para servidores pequeños o para inspeccionar los artifacts a mano.
/// </summary>
public sealed class FileSystemArtifactStore(string directory) : IArtifactStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

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
        return await ReadAsync(folder, version.Value, cancellationToken).ConfigureAwait(false);
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
            if (await ReadAsync(folder, v, cancellationToken).ConfigureAwait(false) is { } artifact)
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

        var list = new List<Artifact>();
        foreach (var folder in Directory.GetDirectories(directory))
        {
            if (await GetAsync(Path.GetFileName(folder), cancellationToken: cancellationToken).ConfigureAwait(false) is { } artifact
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

    private static async Task<Artifact?> ReadAsync(string folder, int version, CancellationToken cancellationToken)
    {
        var json = Path.Combine(folder, $"v{version}.json");
        if (!File.Exists(json))
        {
            return null;
        }

        var artifact = JsonSerializer.Deserialize<Artifact>(await File.ReadAllTextAsync(json, cancellationToken).ConfigureAwait(false), JsonSchema.DefaultSerializerOptions)!;
        var bin = Path.Combine(folder, $"v{version}.bin");
        return File.Exists(bin) ? artifact with { Data = await File.ReadAllBytesAsync(bin, cancellationToken).ConfigureAwait(false) } : artifact;
    }
}

/// <summary>Opciones de <see cref="ArtifactTools"/>.</summary>
public sealed class ArtifactToolOptions
{
    /// <summary>Tipos permitidos (por defecto todos los de texto).</summary>
    public IReadOnlyList<string> AllowedTypes { get; init; } = ArtifactTypes.Text;

    /// <summary>Tamaño máximo del contenido en caracteres.</summary>
    public int MaxContentLength { get; init; } = 500_000;

    /// <summary>URL pública de un artifact (p. ej. <c>a => $"/artifacts/{a.Id}"</c>), que se devuelve al modelo y a la UI.</summary>
    public Func<Artifact, string?>? Url { get; init; }

    /// <summary>Clave de <see cref="ToolExecutionContext.Context"/> con el id de conversación.</summary>
    public string ConversationIdKey { get; init; } = "conversationId";

    /// <summary>Incluye el contenido completo en el data part <c>data-artifact</c> que recibe la interfaz.</summary>
    public bool SendContentToUI { get; init; } = true;

    /// <summary>Validación adicional antes de guardar (lanza una excepción para rechazar).</summary>
    public Action<Artifact>? Validate { get; init; }
}

/// <summary>
/// Herramientas para que un agente cree y edite artifacts como Claude: contenido sustancial y
/// autocontenido (páginas HTML, documentos, código, diagramas, componentes React) que se muestra
/// aparte del chat, se versiona y se puede editar con reemplazos exactos.
/// </summary>
/// <remarks>
/// Cada cambio se envía a la interfaz como data part <c>data-artifact</c> (reconciliado por id),
/// así <c>useChat</c> puede mostrarlo en un panel lateral.
/// </remarks>
public static class ArtifactTools
{
    /// <summary>Instrucciones de sistema recomendadas para agentes que crean artifacts.</summary>
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

        string? Conversation(ToolExecutionContext ctx) =>
            ctx.Context is not null && ctx.Context.TryGetValue(o.ConversationIdKey, out var value) ? value?.ToString() : null;

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

        async Task<Artifact> LoadAsync(string id, ToolExecutionContext ctx, CancellationToken ct)
        {
            var artifact = await store.GetAsync(id, cancellationToken: ct).ConfigureAwait(false)
                           ?? throw new ArgumentException($"No existe el artifact '{id}'.");
            var conversation = Conversation(ctx);
            return conversation is not null && artifact.ConversationId is not null && artifact.ConversationId != conversation
                ? throw new UnauthorizedAccessException($"El artifact '{id}' pertenece a otra conversación.")
                : artifact;
        }

        var allowed = string.Join(", ", o.AllowedTypes);
        return
        [
            Tool.Create<CreateInput, object>(
                "create_artifact",
                $"Crea un artifact nuevo. Tipos: {allowed}.",
                async (input, ctx, ct) =>
                {
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
                        new Artifact { Id = id, Title = input.Title, Type = input.Type, Language = input.Language, Content = input.Content, ConversationId = Conversation(ctx) },
                        "created",
                        ctx,
                        ct).ConfigureAwait(false);
                }),
            Tool.Create<UpdateInput, object>(
                "update_artifact",
                "Edita un artifact reemplazando un fragmento exacto (oldStr debe aparecer una sola vez). Úsalo para cambios pequeños.",
                async (input, ctx, ct) =>
                {
                    var current = await LoadAsync(input.Id, ctx, ct).ConfigureAwait(false);
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
                    var current = await LoadAsync(input.Id, ctx, ct).ConfigureAwait(false);
                    return await SaveAsync(current with { Content = input.Content, Title = input.Title ?? current.Title }, "rewritten", ctx, ct).ConfigureAwait(false);
                }),
            Tool.Create<ReadInput, object>(
                "read_artifact",
                "Lee el contenido actual (o una versión) de un artifact.",
                async (input, ctx, ct) =>
                {
                    var current = await LoadAsync(input.Id, ctx, ct).ConfigureAwait(false);
                    if (input.Version is { } v)
                    {
                        current = await store.GetAsync(input.Id, v, ct).ConfigureAwait(false) ?? throw new ArgumentException($"No existe la versión {v}.");
                    }

                    return new { id = current.Id, title = current.Title, type = current.Type, version = current.Version, content = current.Data is null ? current.Content : $"[archivo binario {current.FileName}, {current.Data.Length} bytes]" };
                }),
            Tool.Create<ListInput, object>(
                "list_artifacts",
                "Lista los artifacts de la conversación.",
                async (_, ctx, ct) => (await store.ListAsync(Conversation(ctx), ct).ConfigureAwait(false))
                    .Select(a => new { id = a.Id, title = a.Title, type = a.Type, version = a.Version, url = o.Url?.Invoke(a) })
                    .ToList()),
        ];
    }

    /// <summary>Guarda un archivo binario generado (Word, Excel, PDF...) como artifact y lo anuncia a la interfaz.</summary>
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
        options ??= new ArtifactToolOptions();
        var conversation = context?.Context is not null && context.Context.TryGetValue(options.ConversationIdKey, out var value) ? value?.ToString() : null;
        var id = Slug(Path.GetFileNameWithoutExtension(fileName));
        if (await store.GetAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false) is { } existing && existing.ConversationId != conversation)
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
            ConversationId = conversation,
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

    /// <summary>Id legible y seguro para URLs a partir de un título.</summary>
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
