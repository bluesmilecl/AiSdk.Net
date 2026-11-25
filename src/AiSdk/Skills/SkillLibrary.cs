using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using AiSdk.Http;

namespace AiSdk.Skills;

/// <summary>
/// Conjunto de skills disponibles para los agentes. Carga skills desde carpetas, archivos ZIP,
/// repositorios de GitHub o recursos embebidos, y genera el catálogo que ve el modelo.
/// </summary>
/// <example>
/// <code>
/// var skills = new SkillLibrary();
/// skills.LoadDirectory("skills", new() { Trusted = true });           // skills propias
/// await skills.LoadFromGitHubAsync("anthropics/skills", "skills");    // skills públicas
/// var agent = new ToolLoopAgent(new() { Model = claude, Skills = skills });
/// </code>
/// </example>
public sealed class SkillLibrary
{
    /// <summary>
    /// Marcador de procedencia que se guarda junto al <c>SKILL.md</c> de las skills no confiables (p. ej. las
    /// creadas por un agente con <c>create_skill</c>). Al cargar, una skill con este archivo es siempre no
    /// confiable, aunque la carpeta se cargue con <see cref="SkillLoadOptions.Trusted"/>: así una inyección de
    /// prompt no se convierte en código de confianza al reiniciar. Para confiar en ella, revísela y borre el archivo.
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

    /// <summary>Skills ordenadas por nombre.</summary>
    public IReadOnlyList<Skill> Skills => _skills.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    public int Count => _skills.Count;

    /// <summary>Errores de skills omitidas al cargar (con <see cref="SkillLoadOptions.ThrowOnInvalid"/> = false).</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors.ToList();

    /// <summary>Activaciones por skill desde que se creó la librería.</summary>
    public IReadOnlyDictionary<string, int> Usage => _usage.Snapshot();

    /// <summary>Se dispara cada vez que el modelo o el usuario activa una skill.</summary>
    public event Action<SkillActivation>? Activated;

    public Skill? Get(string name) => _skills.TryGetValue(name, out var skill) ? skill : null;

    public bool Contains(string name) => _skills.ContainsKey(name);

    /// <summary>
    /// Agrega o reemplaza explícitamente una skill (la valida: una skill inválida lanza <see cref="FormatException"/>,
    /// salvo con <paramref name="strict"/> = false). Los métodos <c>Load*</c>, en cambio, no reemplazan skills ya cargadas.
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
    /// Carga todas las skills bajo <paramref name="root"/> (cada carpeta con un <c>SKILL.md</c>,
    /// a cualquier profundidad, p. ej. <c>.claude/skills</c>, <c>plugins/*/skills</c>).
    /// </summary>
    public IReadOnlyList<Skill> LoadDirectory(string root, SkillLoadOptions? options = null)
    {
        options ??= SkillLoadOptions.Default;
        var loaded = new List<Skill>();
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"No existe la carpeta de skills '{root}'.");
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
                    if (new FileInfo(skillMd).Length > options.MaxFileBytes)
                    {
                        throw new InvalidDataException($"El SKILL.md supera el tamaño máximo ({options.MaxFileBytes} bytes).");
                    }

                    var files = SkillFiles.FromDirectory(folder, options.MaxFileBytes);
                    if (files.List().Count > options.MaxFiles)
                    {
                        throw new InvalidDataException($"La skill tiene más de {options.MaxFiles} archivos.");
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

    /// <summary>True si la carpeta tiene el marcador <see cref="ProvenanceFileName"/> (aunque sea un enlace).</summary>
    private static bool HasProvenanceMarker(string folder)
    {
        var marker = Path.Combine(folder, ProvenanceFileName);
        return File.Exists(marker) || Directory.Exists(marker) || new FileInfo(marker).LinkTarget is not null;
    }

    /// <summary>Carga skills desde un ZIP (paquete de una skill o un repositorio completo).</summary>
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
    /// Descarga un repositorio público de GitHub y carga sus skills (p. ej. <c>anthropics/skills</c>,
    /// <c>dotnet/skills</c>). Las skills de terceros se cargan como no confiables salvo que se indique.
    /// </summary>
    /// <param name="repository">"propietario/repositorio".</param>
    /// <param name="path">Subcarpeta opcional (p. ej. <c>skills</c> o <c>plugins/dotnet-test/skills</c>).</param>
    /// <param name="reference">Rama, tag o commit (fíjelo a un commit para builds reproducibles).</param>
    /// <param name="options">Límites y confianza (por defecto, no confiable).</param>
    /// <param name="httpClient">Cliente HTTP (por defecto, el compartido del SDK).</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
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
            throw new ArgumentException("Use el formato 'propietario/repositorio'.", nameof(repository));
        }

        options ??= SkillLoadOptions.Default;
        var url = new Uri($"https://codeload.github.com/{repository}/zip/{Uri.EscapeDataString(reference)}");
        using var response = await (httpClient ?? ProviderHttp.DefaultClient)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"No se encontró {repository}@{reference} en GitHub (¿repositorio privado o rama inexistente?).");
        }

        response.EnsureSuccessStatusCode();
        var tooLarge = $"El ZIP de {repository}@{reference} supera el tamaño máximo de {options.MaxArchiveBytes} bytes (SkillLoadOptions.MaxArchiveBytes).";
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
    /// Carga skills incluidas como recursos embebidos en un ensamblado: agregue la carpeta al proyecto con
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

                _loadErrors.Enqueue($"{location}: aceptada en modo no estricto: {string.Join(" ", validation.Errors)}");
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
                    $"Ya hay una skill '{skill.Name}'{(existing is null ? string.Empty : $" (de {existing})")}; se conserva la primera. Use SkillLoadOptions.ReplaceExisting para reemplazarla.");
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
    /// Escribe una skill en <c>{directorio}/{nombre}/</c> (SKILL.md y archivos). La confianza se conserva en disco:
    /// si la skill no es de confianza se escribe el marcador <see cref="ProvenanceFileName"/> (y al volver a
    /// cargarla seguirá sin serlo); si lo es, se elimina el marcador que hubiera.
    /// </summary>
    public static Task<string> SaveAsync(Skill skill, string directory, CancellationToken cancellationToken = default) =>
        SaveAsync(skill, directory, skill.Trusted ? null : "untrusted", cancellationToken);

    /// <summary>Escribe la skill; con <paramref name="untrustedSource"/> deja el marcador de procedencia.</summary>
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
                ["note"] = "Skill no confiable: revísela y borre este archivo para confiar en ella.",
            };
            await File.WriteAllTextAsync(marker, provenance.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), skill.ToMarkdown(), cancellationToken).ConfigureAwait(false);
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
            await File.WriteAllBytesAsync(target, data, cancellationToken).ConfigureAwait(false);
        }

        if (untrustedSource is null && File.Exists(marker))
        {
            File.Delete(marker);
        }

        return folder;
    }

    /// <summary>Impide escribir a través de un enlace simbólico o junction que apunte fuera de la carpeta.</summary>
    private static void EnsureNoLinks(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (new FileInfo(current).LinkTarget is not null)
            {
                throw new IOException($"No se escribe la skill a través del enlace '{current}'.");
            }
        }
    }

    /// <summary>Empaqueta una skill en ZIP (<c>nombre/SKILL.md</c>...), el formato que aceptan Claude.ai y la API de Skills.</summary>
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
    /// Catálogo para el prompt de sistema: nombre, descripción y ubicación de cada skill invocable por el
    /// modelo, en el formato <c>&lt;available_skills&gt;</c> que usan Claude Code y Copilot.
    /// Si supera <paramref name="maxChars"/>, las últimas skills se listan sin descripción.
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

    /// <summary>Instrucciones de sistema que explican al modelo cómo usar las skills, con el catálogo.</summary>
    public string SystemPrompt(int maxCatalogChars = 15_000) =>
        $"""
        Tienes skills: paquetes de instrucciones y recursos para tareas especializadas. Cuando una tarea coincida
        con la descripción de una skill, actívala con load_skill ANTES de responder y sigue sus instrucciones.
        Lee los archivos que mencione (references/, assets/) con read_skill_file solo cuando los necesites.
        No inventes el contenido de una skill que no cargaste.

        {Catalog(maxCatalogChars)}
        """;

    /// <summary>
    /// Invocación explícita por el usuario (como <c>/nombre argumentos</c>): devuelve las instrucciones con
    /// <c>$ARGUMENTS</c> reemplazado, listas para enviarse como mensaje.
    /// </summary>
    public string Invoke(string name, string? arguments = null)
    {
        var skill = Get(name) ?? throw new KeyNotFoundException($"No existe la skill '{name}'.");
        RecordActivation(name, arguments);
        var instructions = skill.Instructions.Contains("$ARGUMENTS", StringComparison.Ordinal)
            ? skill.Instructions.Replace("$ARGUMENTS", arguments ?? string.Empty, StringComparison.Ordinal)
            : arguments is { Length: > 0 } ? $"{skill.Instructions}\n\nARGUMENTS: {arguments}" : skill.Instructions;
        return $"<skill name=\"{skill.Name}\">\n{instructions}\n</skill>";
    }

    /// <summary>Interpreta un comando de barra (<c>/skill-name argumentos</c>); null si el texto no es uno.</summary>
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
