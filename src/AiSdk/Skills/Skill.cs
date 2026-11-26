using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using AiSdk.Resources;
using System.Globalization;

namespace AiSdk.Skills;

/// <summary>
/// A skill according to the <see href="https://agentskills.io">Agent Skills</see> open standard
/// (Claude, Copilot, Codex, Cursor...): a folder with <c>SKILL.md</c> (name, description and
/// instructions) and, optionally, <c>references/</c>, <c>scripts/</c> and <c>assets/</c>.
/// </summary>
/// <remarks>
/// The agent only sees the name and description of each skill (≈100 tokens); it loads the instructions when
/// activating it and the reference files only when it needs them (progressive disclosure).
/// </remarks>
public sealed record Skill
{
    /// <summary>
    /// Gets the name: 1-64 characters, lowercase letters, digits and hyphens; equal to the folder name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what the skill does and when to use it (1-1024 characters). It is what the model uses to decide
    /// whether to activate the skill.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>Gets the Markdown body of <c>SKILL.md</c> with the instructions.</summary>
    public required string Instructions { get; init; }

    public string? License { get; init; }

    /// <summary>
    /// Gets the environment requirements (max. 500 characters), e.g. "Requires Python 3 and internet access".
    /// </summary>
    public string? Compatibility { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the pre-approved tools (<c>allowed-tools</c>, experimental in the standard).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Gets other frontmatter fields (client-specific extensions, e.g. <c>disable-model-invocation</c>).</summary>
    public IReadOnlyDictionary<string, object?> Extra { get; init; } = new Dictionary<string, object?>();

    /// <summary>Gets the skill's files (in addition to SKILL.md).</summary>
    public ISkillFiles Files { get; init; } = SkillFiles.Empty;

    /// <summary>Gets the origin (path, URL or resource name), for information only.</summary>
    public string? Location { get; init; }

    /// <summary>
    /// Gets whether the skill is trusted. Third-party skills may contain malicious instructions or scripts:
    /// by default their scripts are not executed (see <see cref="SkillToolOptions.AllowUntrustedScripts"/>).
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>
    /// True if the model can activate the skill on its own (false with <c>disable-model-invocation: true</c>).
    /// </summary>
    public bool ModelInvocable =>
        !(Extra.TryGetValue("disable-model-invocation", out var value) && string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates a skill from the content of <c>SKILL.md</c>.</summary>
    public static Skill Parse(string skillMarkdown, ISkillFiles? files = null, string? location = null, bool trusted = false)
    {
        var (frontmatter, body) = SkillMarkdown.Parse(skillMarkdown);
        if (frontmatter is null)
        {
            throw new FormatException(Strings.Skill_NoFrontmatter);
        }

        string? Text(string key) => frontmatter.TryGetValue(key, out var v) ? v as string : null;

        var known = new HashSet<string>(StringComparer.Ordinal) { "name", "description", "license", "compatibility", "metadata", "allowed-tools" };
        return new Skill
        {
            Name = Text("name") ?? throw new FormatException(Strings.Skill_MissingName),
            Description = Text("description") ?? throw new FormatException(Strings.Skill_MissingDescription),
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

    /// <summary>Returns the content of <c>SKILL.md</c>.</summary>
    public string ToMarkdown() => SkillMarkdown.Write(this);

    /// <summary>Validates against the standard (name, description, sizes, references).</summary>
    public SkillValidationResult Validate(string? directoryName = null) => SkillValidator.Validate(this, directoryName);
}

/// <summary>Files of a skill (relative paths with <c>/</c>, not including <c>SKILL.md</c>).</summary>
public interface ISkillFiles
{
    IReadOnlyList<string> List();

    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Implementations of <see cref="ISkillFiles"/>.</summary>
public static class SkillFiles
{
    public static ISkillFiles Empty { get; } = new InMemory(new Dictionary<string, byte[]>());

    public static ISkillFiles FromDictionary(IReadOnlyDictionary<string, byte[]> files) => new InMemory(files);

    public static ISkillFiles FromText(IReadOnlyDictionary<string, string> files) =>
        new InMemory(files.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value)));

    /// <summary>
    /// Files read from a folder. Does not follow symbolic links or reparse points (junctions): a linked
    /// file or folder inside the skill is ignored, so that it cannot expose files from outside.
    /// </summary>
    /// <param name="directory">Skill folder (it may itself be a link).</param>
    /// <param name="maxFileBytes">Maximum size of a file when reading it.</param>
    public static ISkillFiles FromDirectory(string directory, long maxFileBytes = long.MaxValue) => new Directory(directory, maxFileBytes);

    /// <summary>Normalizes a relative path and rejects absolute paths or paths that escape the skill.</summary>
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
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_InvalidPath, path), nameof(path));
        }

        return normalized;
    }

    /// <summary>Enumeration options that neither enter linked folders nor return linked files.</summary>
    internal static EnumerationOptions WithoutLinks { get; } = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// Real path of <paramref name="relative"/> under <paramref name="root"/> if every component exists and none is a
    /// link (<see cref="FileAttributes.ReparsePoint"/>); null otherwise. With the path already normalized (no <c>..</c>)
    /// and no links, the result is necessarily contained in <paramref name="root"/>.
    /// </summary>
    internal static string? ResolveInside(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (LinkOrMissing(current))
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>True if the path does not exist or is a link (even if it points to a nonexistent target).</summary>
    internal static bool LinkOrMissing(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private sealed class InMemory(IReadOnlyDictionary<string, byte[]> files) : ISkillFiles
    {
        private readonly Dictionary<string, byte[]> _files = files.ToDictionary(kv => NormalizePath(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        public IReadOnlyList<string> List() => _files.Keys.Where(k => k != "SKILL.md").Order(StringComparer.Ordinal).ToList();

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.TryGetValue(NormalizePath(path), out var data) ? data : null);
    }

    private sealed class Directory(string root, long maxFileBytes) : ISkillFiles
    {
        private readonly string _root = Path.GetFullPath(root);

        public IReadOnlyList<string> List() =>
            System.IO.Directory.EnumerateFiles(_root, "*", WithoutLinks)
                .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
                .Where(f => f != "SKILL.md" && !f.Split('/').Any(s => s.StartsWith('.')))
                .Order(StringComparer.Ordinal)
                .ToList();

        public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            var relative = NormalizePath(path);
            if (relative.Split('/').Any(s => s.StartsWith('.')))
            {
                return null; // los archivos ocultos no forman parte de la skill (List tampoco los muestra)
            }

            // GetFullPath no resuelve enlaces: se rechaza cualquier componente que sea un enlace simbólico o una
            // junction, así references/x.md → /etc/hostname (o una carpeta enlazada) no sale de la skill.
            if (ResolveInside(_root, relative) is not { } full || !File.Exists(full))
            {
                return null;
            }

            var length = new FileInfo(full).Length;
            if (length > maxFileBytes)
            {
                throw new IOException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_FileTooLarge, relative, maxFileBytes));
            }

            // Longitud 0: no se abre (un FIFO también mide 0 y bloquearía la lectura hasta que alguien escriba).
            return length == 0 ? [] : await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Limits for loading third-party skills.</summary>
public sealed record SkillLoadOptions
{
    /// <summary>
    /// Gets or sets whether the loaded skills are marked as trusted (their scripts can run). A skill with the
    /// <see cref="SkillLibrary.ProvenanceFileName"/> provenance marker (e.g. created by an agent with
    /// <c>create_skill</c>) is always loaded as untrusted, even if it is in a trusted folder.
    /// </summary>
    public bool Trusted { get; init; }

    /// <summary>Gets or sets the maximum size per file (the standard recommends 5 MB).</summary>
    public long MaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Gets or sets the maximum number of files per skill.</summary>
    public int MaxFiles { get; init; } = 500;

    /// <summary>
    /// Gets or sets the maximum size of a ZIP (or of a GitHub repository download). Defaults to 100 MB.
    /// </summary>
    public long MaxArchiveBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the total decompressed bytes accepted from a ZIP (protects against "ZIP bombs").
    /// Defaults to 256 MB.
    /// </summary>
    public long MaxArchiveExtractedBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>
    /// Gets or sets whether skills that do not comply with the standard are rejected (true by default). With false,
    /// skills that have a name and description are accepted even if they exceed limits (some published skills exceed
    /// the 1024 description characters); the problems are reported in <see cref="SkillLibrary.LoadErrors"/>.
    /// </summary>
    public bool Strict { get; init; } = true;

    /// <summary>If true, an invalid skill throws an exception; if false, it is skipped and reported in <see cref="SkillLibrary.LoadErrors"/>.</summary>
    public bool ThrowOnInvalid { get; init; }

    /// <summary>
    /// If false (the default), a skill with the same name as one already loaded is skipped and reported in
    /// <see cref="SkillLibrary.LoadErrors"/> (or an <see cref="InvalidOperationException"/> is thrown with
    /// <see cref="ThrowOnInvalid"/>): the first one is kept, so that a later load (e.g. a GitHub repository) does not
    /// silently replace one of your own skills. If true, the new one replaces the previous one (useful for reloading).
    /// </summary>
    public bool ReplaceExisting { get; init; }

    public static SkillLoadOptions Default { get; } = new();
}

/// <summary>
/// Skill read from a ZIP, or the error that prevented reading it (reported per skill, without stopping the load).
/// </summary>
internal sealed record ArchivedSkill(string Folder, string? Markdown, Dictionary<string, byte[]>? Files, bool Untrusted, Exception? Error);

/// <summary>Loads skills from ZIP files (.skill / .zip packages, GitHub repositories).</summary>
internal static class SkillArchive
{
    /// <summary>Reads all the skills from a ZIP; <paramref name="prefix"/> restricts it to a subfolder.</summary>
    public static List<ArchivedSkill> Read(Stream zip, string? prefix, SkillLoadOptions options)
    {
        if (zip.CanSeek && zip.Length - zip.Position > options.MaxArchiveBytes)
        {
            throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_ZipTooLarge, options.MaxArchiveBytes));
        }

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true); // el stream es del llamador
        var entries = archive.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).ToList();

        // Los ZIP de GitHub tienen una carpeta raíz "repo-rama/": se descarta si todas las entradas la comparten.
        var root = CommonRoot(entries.Select(e => e.FullName));
        var normalizedPrefix = prefix?.Trim('/') is { Length: > 0 } p ? p + "/" : string.Empty;

        var skills = new List<ArchivedSkill>();
        var folders = entries.Where(e => e.Name == "SKILL.md").Select(e => e.FullName[..^"SKILL.md".Length]).ToList();
        var names = entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        var extracted = 0L;

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
            var folderName = folder.TrimEnd('/').Split('/')[^1];
            try
            {
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
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_ArchivedSkillTooLarge, folder, path));
                    }

                    files[path] = ReadBounded(entry, options.MaxFileBytes, string.Format(CultureInfo.CurrentCulture, Strings.Skill_ArchivedSkillTooLarge, folder, path));
                    extracted += files[path].Length;
                    if (extracted > options.MaxArchiveExtractedBytes)
                    {
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_ZipExtractedTooLarge, options.MaxArchiveExtractedBytes));
                    }
                }

                var markdown = Encoding.UTF8.GetString(ReadBounded(skillMd, options.MaxFileBytes, string.Format(CultureInfo.CurrentCulture, Strings.Skill_ArchivedMarkdownTooLarge, folder)));
                var untrusted = names.Contains(folder + SkillLibrary.ProvenanceFileName);
                skills.Add(new ArchivedSkill(folderName, markdown, files, untrusted, null));
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("El ZIP supera", StringComparison.Ordinal))
            {
                throw; // límite global: no tiene sentido seguir con las demás skills
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skills.Add(new ArchivedSkill(folderName, null, null, true, ex));
            }
        }

        return skills;
    }

    /// <summary>Reads an entry without trusting the size declared in the ZIP (it may lie).</summary>
    private static byte[] ReadBounded(ZipArchiveEntry entry, long maxBytes, string error)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException(error);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
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

/// <summary>Record of skill usage (for metrics or the UI).</summary>
public sealed record SkillActivation(string Skill, DateTimeOffset At, string? Arguments = null);

/// <summary>Concurrent counter of activations per skill.</summary>
internal sealed class SkillUsage
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public void Add(string skill) => _counts.AddOrUpdate(skill, 1, (_, c) => c + 1);

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(_counts);
}
