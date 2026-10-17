using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace AiSdk.Skills;

/// <summary>
/// Una skill según el estándar abierto <see href="https://agentskills.io">Agent Skills</see>
/// (Claude, Copilot, Codex, Cursor...): una carpeta con <c>SKILL.md</c> (nombre, descripción e
/// instrucciones) y, opcionalmente, <c>references/</c>, <c>scripts/</c> y <c>assets/</c>.
/// </summary>
/// <remarks>
/// El agente ve solo nombre y descripción de cada skill (≈100 tokens); carga las instrucciones al
/// activarla y los archivos de referencia solo cuando los necesita (divulgación progresiva).
/// </remarks>
public sealed record Skill
{
    /// <summary>Nombre: 1-64 caracteres, minúsculas, números y guiones; igual al nombre de la carpeta.</summary>
    public required string Name { get; init; }

    /// <summary>Qué hace y cuándo usarla (1-1024 caracteres). Es lo que el modelo usa para decidir activarla.</summary>
    public required string Description { get; init; }

    /// <summary>Cuerpo Markdown de <c>SKILL.md</c> con las instrucciones.</summary>
    public required string Instructions { get; init; }

    public string? License { get; init; }

    /// <summary>Requisitos de entorno (máx. 500 caracteres), p. ej. "Requiere Python 3 y acceso a internet".</summary>
    public string? Compatibility { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Herramientas preaprobadas (<c>allowed-tools</c>, experimental en el estándar).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Otros campos del frontmatter (extensiones de cada cliente, p. ej. <c>disable-model-invocation</c>).</summary>
    public IReadOnlyDictionary<string, object?> Extra { get; init; } = new Dictionary<string, object?>();

    /// <summary>Archivos de la skill (además de SKILL.md).</summary>
    public ISkillFiles Files { get; init; } = SkillFiles.Empty;

    /// <summary>Origen (ruta, URL o nombre de recurso), informativo.</summary>
    public string? Location { get; init; }

    /// <summary>
    /// Si es de confianza. Las skills de terceros pueden contener instrucciones o scripts maliciosos:
    /// por defecto sus scripts no se ejecutan (ver <see cref="SkillToolOptions.AllowUntrustedScripts"/>).
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>True si el modelo puede activarla por sí mismo (false con <c>disable-model-invocation: true</c>).</summary>
    public bool ModelInvocable =>
        !(Extra.TryGetValue("disable-model-invocation", out var value) && string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Crea una skill a partir del contenido de <c>SKILL.md</c>.</summary>
    public static Skill Parse(string skillMarkdown, ISkillFiles? files = null, string? location = null, bool trusted = false)
    {
        var (frontmatter, body) = SkillMarkdown.Parse(skillMarkdown);
        if (frontmatter is null)
        {
            throw new FormatException("SKILL.md no tiene frontmatter YAML (--- name/description ---).");
        }

        string? Text(string key) => frontmatter.TryGetValue(key, out var v) ? v as string : null;

        var known = new HashSet<string>(StringComparer.Ordinal) { "name", "description", "license", "compatibility", "metadata", "allowed-tools" };
        return new Skill
        {
            Name = Text("name") ?? throw new FormatException("Falta 'name' en el frontmatter de SKILL.md."),
            Description = Text("description") ?? throw new FormatException("Falta 'description' en el frontmatter de SKILL.md."),
            Instructions = body,
            License = Text("license"),
            Compatibility = Text("compatibility"),
            Metadata = frontmatter.TryGetValue("metadata", out var m) && m is IReadOnlyDictionary<string, string> map ? map : new Dictionary<string, string>(),
            AllowedTools = frontmatter.TryGetValue("allowed-tools", out var t) switch
            {
                true when t is string s => s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries),
                true when t is IEnumerable<string> list => list.ToList(),
                _ => [],
            },
            Extra = frontmatter.Where(kv => !known.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Files = files ?? SkillFiles.Empty,
            Location = location,
            Trusted = trusted,
        };
    }

    /// <summary>Contenido de <c>SKILL.md</c>.</summary>
    public string ToMarkdown() => SkillMarkdown.Write(this);

    /// <summary>Valida contra el estándar (nombre, descripción, tamaños, referencias).</summary>
    public SkillValidationResult Validate(string? directoryName = null) => SkillValidator.Validate(this, directoryName);
}

/// <summary>Archivos de una skill (rutas relativas con <c>/</c>, sin incluir <c>SKILL.md</c>).</summary>
public interface ISkillFiles
{
    IReadOnlyList<string> List();

    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Implementaciones de <see cref="ISkillFiles"/>.</summary>
public static class SkillFiles
{
    public static ISkillFiles Empty { get; } = new InMemory(new Dictionary<string, byte[]>());

    public static ISkillFiles FromDictionary(IReadOnlyDictionary<string, byte[]> files) => new InMemory(files);

    public static ISkillFiles FromText(IReadOnlyDictionary<string, string> files) =>
        new InMemory(files.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value)));

    public static ISkillFiles FromDirectory(string directory) => new Directory(directory);

    /// <summary>Normaliza una ruta relativa y rechaza rutas absolutas o que salgan de la skill.</summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is ".." or ""))
        {
            throw new ArgumentException($"Ruta inválida dentro de la skill: '{path}'.", nameof(path));
        }

        return normalized;
    }

    private sealed class InMemory(IReadOnlyDictionary<string, byte[]> files) : ISkillFiles
    {
        private readonly Dictionary<string, byte[]> _files = files.ToDictionary(kv => NormalizePath(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        public IReadOnlyList<string> List() => _files.Keys.Where(k => k != "SKILL.md").Order(StringComparer.Ordinal).ToList();

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.TryGetValue(NormalizePath(path), out var data) ? data : null);
    }

    private sealed class Directory(string root) : ISkillFiles
    {
        private readonly string _root = Path.GetFullPath(root);

        public IReadOnlyList<string> List() =>
            System.IO.Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
                .Where(f => f != "SKILL.md" && !f.Split('/').Any(s => s.StartsWith('.')))
                .Order(StringComparer.Ordinal)
                .ToList();

        public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            var full = Path.GetFullPath(Path.Combine(_root, NormalizePath(path)));
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            {
                return null;
            }

            return await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Límites al cargar skills de terceros.</summary>
public sealed record SkillLoadOptions
{
    /// <summary>Marca las skills cargadas como de confianza (sus scripts podrán ejecutarse).</summary>
    public bool Trusted { get; init; }

    /// <summary>Tamaño máximo por archivo (el estándar recomienda 5 MB).</summary>
    public long MaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Archivos máximos por skill.</summary>
    public int MaxFiles { get; init; } = 500;

    /// <summary>
    /// Con true (por defecto) se rechazan las skills que no cumplen el estándar. Con false se aceptan las que
    /// tienen nombre y descripción, aunque incumplan límites (algunas skills publicadas superan los 1024
    /// caracteres de descripción); los problemas quedan en <see cref="SkillLibrary.LoadErrors"/>.
    /// </summary>
    public bool Strict { get; init; } = true;

    /// <summary>Si es true, una skill inválida lanza excepción; si es false, se omite y se informa en <see cref="SkillLibrary.LoadErrors"/>.</summary>
    public bool ThrowOnInvalid { get; init; }

    public static SkillLoadOptions Default { get; } = new();
}

/// <summary>Carga de skills desde ZIP (paquetes .skill / .zip, repositorios de GitHub).</summary>
internal static class SkillArchive
{
    /// <summary>Lee todas las skills de un ZIP; <paramref name="prefix"/> limita a una subcarpeta.</summary>
    public static List<(string Folder, string Markdown, Dictionary<string, byte[]> Files)> Read(Stream zip, string? prefix, SkillLoadOptions options)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).ToList();

        // Los ZIP de GitHub tienen una carpeta raíz "repo-rama/": se descarta si todas las entradas la comparten.
        var root = CommonRoot(entries.Select(e => e.FullName));
        var normalizedPrefix = prefix?.Trim('/') is { Length: > 0 } p ? p + "/" : string.Empty;

        var skills = new List<(string, string, Dictionary<string, byte[]>)>();
        var folders = entries.Where(e => e.Name == "SKILL.md").Select(e => e.FullName[..^"SKILL.md".Length]).ToList();

        // Cada archivo pertenece a la skill cuya carpeta es el prefijo más largo (soporta skills anidadas).
        string? Owner(string fullName) => folders.Where(f => fullName.StartsWith(f, StringComparison.Ordinal)).MaxBy(f => f.Length);

        foreach (var skillMd in entries.Where(e => e.Name == "SKILL.md"))
        {
            var relative = skillMd.FullName[root.Length..];
            if (!relative.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var folder = skillMd.FullName[..^"SKILL.md".Length];
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => e.FullName.StartsWith(folder, StringComparison.Ordinal) && e != skillMd && !e.FullName.EndsWith('/')))
            {
                var path = entry.FullName[folder.Length..];
                if (path.Split('/').Any(s => s.StartsWith('.')) || Owner(entry.FullName) != folder)
                {
                    continue; // archivos ocultos o pertenecientes a una skill anidada
                }

                if (entry.Length > options.MaxFileBytes || files.Count >= options.MaxFiles)
                {
                    throw new InvalidDataException($"La skill en '{folder}' supera los límites de tamaño ({path}).");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files[path] = buffer.ToArray();
            }

            using var reader = new StreamReader(skillMd.Open(), Encoding.UTF8);
            skills.Add((folder.TrimEnd('/').Split('/')[^1], reader.ReadToEnd(), files));
        }

        return skills;
    }

    private static string CommonRoot(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var first = list[0].Split('/')[0] + "/";
        return list.All(n => n.StartsWith(first, StringComparison.Ordinal)) && list.Any(n => n.Length > first.Length) && !list.Any(n => n == "SKILL.md")
            ? first
            : string.Empty;
    }
}

/// <summary>Registro de uso de skills (para métricas o la interfaz).</summary>
public sealed record SkillActivation(string Skill, DateTimeOffset At, string? Arguments = null);

/// <summary>Contador concurrente de activaciones por skill.</summary>
internal sealed class SkillUsage
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Add(string skill) => _counts.AddOrUpdate(skill, 1, (_, c) => c + 1);

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(_counts);
}
