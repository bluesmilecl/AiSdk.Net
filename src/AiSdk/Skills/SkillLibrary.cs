using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using AiSdk.Http;
using AiSdk.Resources;
using System.Globalization;

namespace AiSdk.Skills;

/// <summary>
/// Set of skills available to agents. Loads skills from folders, ZIP files,
/// GitHub repositories or embedded resources, and generates the catalog the model sees.
/// </summary>
/// <example>
/// <code>
/// var skills = new SkillLibrary();
/// skills.LoadDirectory("skills", new() { Trusted = true });           // your own skills
/// await skills.LoadFromGitHubAsync("anthropics/skills", "skills");    // public skills
/// var agent = new ToolLoopAgent(new() { Model = claude, Skills = skills });
/// </code>
/// </example>
public sealed class SkillLibrary
{
    /// <summary>
    /// Provenance marker saved next to the <c>SKILL.md</c> of untrusted skills (e.g. those
    /// created by an agent with <c>create_skill</c>). When loading, a skill with this file is always
    /// untrusted, even if the folder is loaded with <see cref="SkillLoadOptions.Trusted"/>: this way a prompt
    /// injection does not become trusted code on restart. To trust the skill, review it and delete the file.
    /// </summary>
    public const string ProvenanceFileName = ".aisdk-provenance.json";

    private readonly ConcurrentDictionary<string, Skill> _skills = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _loadErrors = new();
    private readonly SkillUsage _usage = new();

    public SkillLibrary()
    {
    }

    public SkillLibrary(IEnumerable<Skill> skills)
    {
        foreach (var skill in skills)
        {
            Add(skill);
        }
    }

    /// <summary>Gets the skills sorted by name.</summary>
    public IReadOnlyList<Skill> Skills => _skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    public int Count => _skills.Count;

    /// <summary>
    /// Gets the errors of skills skipped during loading (with <see cref="SkillLoadOptions.ThrowOnInvalid"/> = false).
    /// </summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors.ToList();

    /// <summary>Gets the activations per skill since the library was created.</summary>
    public IReadOnlyDictionary<string, int> Usage => _usage.Snapshot();

    /// <summary>Raised each time the model or the user activates a skill.</summary>
    public event Action<SkillActivation>? Activated;

    public Skill? Get(string name) => _skills.TryGetValue(name, out var skill) ? skill : null;

    public bool Contains(string name) => _skills.ContainsKey(name);

    /// <summary>
    /// Explicitly adds or replaces a skill (it validates it: an invalid skill throws <see cref="FormatException"/>,
    /// unless <paramref name="strict"/> is false). The <c>Load*</c> methods, by contrast, do not replace skills already loaded.
    /// </summary>
    public SkillLibrary Add(Skill skill, bool strict = true)
    {
        if (strict)
        {
            skill.Validate().ThrowIfInvalid(skill.Name);
        }

        _skills[skill.Name] = skill;
        return this;
    }

    public bool Remove(string name) => _skills.TryRemove(name, out _);

    internal void RecordActivation(string name, string? arguments = null)
    {
        _usage.Add(name);
        Activated?.Invoke(new SkillActivation(name, DateTimeOffset.UtcNow, arguments));
    }

    // ---------------------------------------------------------------------
    // Carga
    // ---------------------------------------------------------------------

    /// <summary>
    /// Loads all the skills under <paramref name="root"/> (every folder with a <c>SKILL.md</c>,
    /// at any depth, e.g. <c>.claude/skills</c>, <c>plugins/*/skills</c>).
    /// </summary>
    public IReadOnlyList<Skill> LoadDirectory(string root, SkillLoadOptions? options = null)
    {
        options ??= SkillLoadOptions.Default;
        var loaded = new List<Skill>();
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_DirectoryNotFound, root));
        }

        // No se siguen enlaces simbólicos ni junctions (ni carpetas ni SKILL.md enlazados): una skill no puede
        // "traer" archivos de fuera de la carpeta cargada.
        foreach (var skillMd in Directory.EnumerateFiles(root, "SKILL.md", SkillFiles.WithoutLinks)
                     .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith('.') && s.Length > 1 && s != ".claude" && s != ".agents" && s != ".github")))
        {
            var folder = Path.GetDirectoryName(skillMd)!;
            TryAdd(
                () =>
                {
                    var length = new FileInfo(skillMd).Length;
                    if (length > options.MaxFileBytes)
                    {
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_MarkdownTooLarge, options.MaxFileBytes));
                    }

                    if (length == 0)
                    {
                        // Vacío o un FIFO (que también mide 0): abrirlo bloquearía la carga hasta que alguien escriba.
                        throw new InvalidDataException(Strings.Skill_MarkdownEmpty);
                    }

                    var files = SkillFiles.FromDirectory(folder, options.MaxFileBytes);
                    if (files.List().Count > options.MaxFiles)
                    {
                        throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_TooManyFiles, options.MaxFiles));
                    }

                    return Skill.Parse(File.ReadAllText(skillMd), files, folder, options.Trusted && !HasProvenanceMarker(folder));
                },
                Path.GetFileName(folder),
                skillMd,
                options,
                loaded);
        }

        return loaded;
    }

    /// <summary>True if the folder has the <see cref="ProvenanceFileName"/> marker (even if it is a link).</summary>
    private static bool HasProvenanceMarker(string folder)
    {
        var marker = Path.Combine(folder, ProvenanceFileName);
        return File.Exists(marker) || Directory.Exists(marker) || new FileInfo(marker).LinkTarget is not null;
    }

    /// <summary>Loads skills from a ZIP (a single skill package or a whole repository).</summary>
    public IReadOnlyList<Skill> LoadZip(Stream zip, string? path = null, SkillLoadOptions? options = null, string? location = null)
    {
        options ??= SkillLoadOptions.Default;
        var loaded = new List<Skill>();
        foreach (var archived in SkillArchive.Read(zip, path, options))
        {
            TryAdd(
                () => archived.Error is { } error
                    ? throw error
                    : Skill.Parse(archived.Markdown!, SkillFiles.FromDictionary(archived.Files!), $"{location ?? "zip"}:{archived.Folder}", options.Trusted && !archived.Untrusted),
                archived.Folder,
                $"{location ?? "zip"}:{archived.Folder}",
                options,
                loaded);
        }

        return loaded;
    }

    /// <summary>
    /// Downloads a public GitHub repository and loads its skills (e.g. <c>anthropics/skills</c>,
    /// <c>dotnet/skills</c>). Third-party skills are loaded as untrusted unless specified otherwise.
    /// </summary>
    /// <param name="repository">"owner/repository".</param>
    /// <param name="path">Optional subfolder (e.g. <c>skills</c> or <c>plugins/dotnet-test/skills</c>).</param>
    /// <param name="reference">Branch, tag or commit (pin it to a commit for reproducible builds).</param>
    /// <param name="options">Limits and trust (untrusted by default).</param>
    /// <param name="httpClient">HTTP client (by default, the SDK's shared one).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<Skill>> LoadFromGitHubAsync(
        string repository,
        string? path = null,
        string reference = "main",
        SkillLoadOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (repository.Count(c => c == '/') != 1 || repository.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')))
        {
            throw new ArgumentException(Strings.Skill_GitHubRepositoryFormat, nameof(repository));
        }

        options ??= SkillLoadOptions.Default;
        var url = new Uri($"https://codeload.github.com/{repository}/zip/{Uri.EscapeDataString(reference)}");
        using var response = await (httpClient ?? ProviderHttp.DefaultClient)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_GitHubNotFound, repository, reference));
        }

        response.EnsureSuccessStatusCode();
        var tooLarge = string.Format(CultureInfo.CurrentCulture, Strings.Skill_GitHubZipTooLarge, repository, reference, options.MaxArchiveBytes);
        if (response.Content.Headers.ContentLength > options.MaxArchiveBytes)
        {
            throw new InvalidDataException(tooLarge);
        }

        // Se copia con límite: el Content-Length puede faltar o mentir.
        using var buffer = new MemoryStream();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > options.MaxArchiveBytes)
                {
                    throw new InvalidDataException(tooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }

        buffer.Position = 0;
        return LoadZip(buffer, path, options, $"github:{repository}@{reference}");
    }

    /// <summary>
    /// Loads skills included as embedded resources in an assembly: add the folder to the project with
    /// <c>&lt;EmbeddedResource Include="skills/**" LogicalName="skills/%(RecursiveDir)%(Filename)%(Extension)" /&gt;</c>.
    /// </summary>
    public IReadOnlyList<Skill> LoadEmbedded(Assembly assembly, string prefix = "skills/", SkillLoadOptions? options = null)
    {
        options ??= new SkillLoadOptions { Trusted = true };
        var names = assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal)).ToList();
        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in names)
            {
                using var source = assembly.GetManifestResourceStream(name)!;
                using var target = archive.CreateEntry(name.Replace('\\', '/')[prefix.Length..]).Open();
                source.CopyTo(target);
            }
        }

        zip.Position = 0;
        return LoadZip(zip, null, options, $"assembly:{assembly.GetName().Name}");
    }

    private void TryAdd(Func<Skill> parse, string folder, string location, SkillLoadOptions options, List<Skill> loaded)
    {
        try
        {
            var skill = parse();
            var validation = skill.Validate(folder);
            if (!validation.IsValid)
            {
                if (options.Strict || string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Description))
                {
                    validation.ThrowIfInvalid(skill.Name);
                }

                _loadErrors.Enqueue(string.Format(CultureInfo.CurrentCulture, Strings.Skill_AcceptedNonStrict, location, string.Join(" ", validation.Errors)));
            }

            if (options.ReplaceExisting)
            {
                _skills[skill.Name] = skill;
            }
            else if (!_skills.TryAdd(skill.Name, skill))
            {
                // Una carga posterior (p. ej. de GitHub) no reemplaza en silencio una skill ya cargada.
                var existing = _skills.TryGetValue(skill.Name, out var current) ? current.Location : null;
                throw new InvalidOperationException(
                    existing is null
                        ? string.Format(CultureInfo.CurrentCulture, Strings.Skill_Duplicate, skill.Name)
                        : string.Format(CultureInfo.CurrentCulture, Strings.Skill_DuplicateFrom, skill.Name, existing));
            }

            loaded.Add(skill);
        }
        catch (Exception ex) when (!options.ThrowOnInvalid && ex is not OperationCanceledException)
        {
            // Cada skill falla por separado (YAML mal formado, rutas inválidas en un ZIP, límites...): una skill
            // rota no impide cargar las demás.
            _loadErrors.Enqueue($"{location}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------
    // Escritura
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes a skill to <c>{directory}/{name}/</c> (SKILL.md and files). Trust is preserved on disk:
    /// if the skill is not trusted, the <see cref="ProvenanceFileName"/> marker is written (and when it is
    /// reloaded it remains untrusted); if it is trusted, any existing marker is removed.
    /// </summary>
    public static Task<string> SaveAsync(Skill skill, string directory, CancellationToken cancellationToken = default) =>
        SaveAsync(skill, directory, skill.Trusted ? null : "untrusted", cancellationToken);

    /// <summary>Writes the skill; with <paramref name="untrustedSource"/> it leaves the provenance marker.</summary>
    internal static async Task<string> SaveAsync(Skill skill, string directory, string? untrustedSource, CancellationToken cancellationToken)
    {
        skill.Validate().ThrowIfInvalid(skill.Name);
        var root = Path.GetFullPath(directory);
        var folder = Path.Combine(root, skill.Name);
        EnsureNoLinks(root, skill.Name);
        Directory.CreateDirectory(folder);

        // El marcador se escribe antes que el contenido: si la escritura falla a medias, lo escrito no queda de confianza.
        var marker = Path.Combine(folder, ProvenanceFileName);
        if (untrustedSource is not null)
        {
            var provenance = new System.Text.Json.Nodes.JsonObject
            {
                ["trusted"] = false,
                ["source"] = untrustedSource,
                ["savedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["note"] = Strings.Skill_ProvenanceNote,
            };
            await WriteReplacingAsync(marker, Encoding.UTF8.GetBytes(provenance.ToJsonString()), cancellationToken).ConfigureAwait(false);
        }

        await WriteReplacingAsync(Path.Combine(folder, "SKILL.md"), Encoding.UTF8.GetBytes(skill.ToMarkdown()), cancellationToken).ConfigureAwait(false);
        foreach (var path in skill.Files.List())
        {
            var data = await skill.Files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                continue;
            }

            var relative = SkillFiles.NormalizePath(path);
            if (relative.Split('/').Any(s => s.StartsWith('.')))
            {
                continue; // los archivos ocultos (como el marcador) no forman parte de la skill
            }

            EnsureNoLinks(folder, relative);
            var target = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await WriteReplacingAsync(target, data, cancellationToken).ConfigureAwait(false);
        }

        if (untrustedSource is null && File.Exists(marker))
        {
            File.Delete(marker);
        }

        return folder;
    }

    /// <summary>
    /// Writes to a hidden temporary file in the same folder and renames it over <paramref name="target"/>. This
    /// replaces the directory entry instead of writing into the existing file: if <paramref name="target"/> is a
    /// hard link to a file outside the skill (indistinguishable from a regular file), a FIFO or a link, the
    /// other file is not modified and the write does not block.
    /// </summary>
    private static async Task WriteReplacingAsync(string target, byte[] data, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // CreateNew (O_EXCL) no sigue un enlace que alguien hubiera dejado con ese nombre.
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    /// <summary>Prevents writing through a symbolic link or junction that points outside the folder.</summary>
    private static void EnsureNoLinks(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (new FileInfo(current).LinkTarget is not null)
            {
                throw new IOException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_WriteThroughLink, current));
            }
        }
    }

    /// <summary>Packs a skill into a ZIP (<c>name/SKILL.md</c>...), the format accepted by Claude.ai and the Skills API.</summary>
    public static async Task<byte[]> PackAsync(Skill skill, CancellationToken cancellationToken = default)
    {
        skill.Validate().ThrowIfInvalid(skill.Name);
        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(archive, $"{skill.Name}/SKILL.md", Encoding.UTF8.GetBytes(skill.ToMarkdown()), cancellationToken).ConfigureAwait(false);
            foreach (var path in skill.Files.List())
            {
                if (await skill.Files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } data)
                {
                    await WriteEntryAsync(archive, $"{skill.Name}/{path}", data, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return zip.ToArray();
    }

    private static async Task WriteEntryAsync(System.IO.Compression.ZipArchive archive, string name, byte[] data, CancellationToken cancellationToken)
    {
        await using var stream = archive.CreateEntry(name).Open();
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Uso
    // ---------------------------------------------------------------------

    /// <summary>
    /// Catalog for the system prompt: name, description and location of each skill the model can
    /// invoke, in the <c>&lt;available_skills&gt;</c> format used by Claude Code and Copilot.
    /// If it exceeds <paramref name="maxChars"/>, the last skills are listed without a description.
    /// </summary>
    public string Catalog(int maxChars = 15_000)
    {
        var builder = new StringBuilder("<available_skills>\n");
        foreach (var skill in Skills.Where(s => s.ModelInvocable))
        {
            var entry = $"<skill>\n<name>{WebUtility.HtmlEncode(skill.Name)}</name>\n<description>{WebUtility.HtmlEncode(skill.Description)}</description>\n</skill>\n";
            builder.Append(builder.Length + entry.Length <= maxChars ? entry : $"<skill><name>{WebUtility.HtmlEncode(skill.Name)}</name></skill>\n");
        }

        return builder.Append("</available_skills>").ToString();
    }

    /// <summary>System instructions that explain to the model how to use the skills, including the catalog.</summary>
    public string SystemPrompt(int maxCatalogChars = 15_000) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Skill_SystemPrompt, Catalog(maxCatalogChars));

    /// <summary>
    /// Explicit invocation by the user (like <c>/name arguments</c>): returns the instructions with
    /// <c>$ARGUMENTS</c> replaced, ready to be sent as a message.
    /// </summary>
    public string Invoke(string name, string? arguments = null)
    {
        var skill = Get(name) ?? throw new KeyNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_NotFound, name));
        RecordActivation(name, arguments);
        var instructions = skill.Instructions.Contains("$ARGUMENTS", StringComparison.Ordinal)
            ? skill.Instructions.Replace("$ARGUMENTS", arguments ?? string.Empty, StringComparison.Ordinal)
            : arguments is { Length: > 0 } ? $"{skill.Instructions}\n\nARGUMENTS: {arguments}" : skill.Instructions;
        return $"<skill name=\"{skill.Name}\">\n{instructions}\n</skill>";
    }

    /// <summary>Interprets a slash command (<c>/skill-name arguments</c>); null if the text is not one.</summary>
    public string? TryInvokeSlashCommand(string userText)
    {
        if (!userText.StartsWith('/'))
        {
            return null;
        }

        var space = userText.IndexOf(' ', StringComparison.Ordinal);
        var name = space < 0 ? userText[1..] : userText[1..space];
        return Contains(name) ? Invoke(name, space < 0 ? null : userText[(space + 1)..].Trim()) : null;
    }
}
