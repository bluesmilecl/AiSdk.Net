using System.ComponentModel;
using System.Text;

namespace AiSdk.Skills;

/// <summary>Fluent construction of skills in code.</summary>
/// <example>
/// <code>
/// var skill = new SkillBuilder("review-contracts")
///     .WithDescription("Reviews Chilean lease agreements. Use it when the user attaches a contract.")
///     .WithInstructions("# Review\n\n1. Identify the parties...")
///     .AddFile("references/clauses.md", clauses)
///     .Build();
/// </code>
/// </example>
public sealed class SkillBuilder(string name)
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private readonly List<string> _allowedTools = [];
    private string _description = string.Empty;
    private string _instructions = string.Empty;
    private string? _license;
    private string? _compatibility;
    private bool _userOnly;

    public SkillBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    public SkillBuilder WithInstructions(string markdown)
    {
        _instructions = markdown;
        return this;
    }

    public SkillBuilder WithLicense(string license)
    {
        _license = license;
        return this;
    }

    public SkillBuilder WithCompatibility(string compatibility)
    {
        _compatibility = compatibility;
        return this;
    }

    public SkillBuilder WithMetadata(string key, string value)
    {
        _metadata[key] = value;
        return this;
    }

    public SkillBuilder AllowTools(params string[] tools)
    {
        _allowedTools.AddRange(tools);
        return this;
    }

    /// <summary>Makes the skill invocable only by the user (<c>disable-model-invocation: true</c>).</summary>
    public SkillBuilder UserInvocableOnly()
    {
        _userOnly = true;
        return this;
    }

    public SkillBuilder AddFile(string path, string content) => AddFile(path, Encoding.UTF8.GetBytes(content));

    public SkillBuilder AddFile(string path, byte[] content)
    {
        _files[SkillFiles.NormalizePath(path)] = content;
        return this;
    }

    /// <summary>Builds the skill (throws <see cref="FormatException"/> if it does not meet the standard).</summary>
    /// <param name="trusted">
    /// Whether the skill is trusted (its scripts can run). Not by default: pass <c>true</c> only for skills you wrote
    /// yourself, never for content that comes from a model, a user or a third party.
    /// </param>
    public Skill Build(bool trusted = false)
    {
        var skill = new Skill
        {
            Name = name,
            Description = _description,
            Instructions = _instructions,
            License = _license,
            Compatibility = _compatibility,
            Metadata = new Dictionary<string, string>(_metadata),
            AllowedTools = _allowedTools.ToList(),
            Extra = _userOnly ? new Dictionary<string, object?> { ["disable-model-invocation"] = "true" } : new Dictionary<string, object?>(),
            Files = SkillFiles.FromDictionary(_files),
            Trusted = trusted,
        };
        skill.Validate().ThrowIfInvalid(name);
        return skill;
    }
}

/// <summary>Drafts skills with a model, following the standard's best practices.</summary>
public static class SkillGenerator
{
    /// <summary>Authoring guide given to the model (a summary of agentskills.io and dotnet/skills).</summary>
    public const string AuthoringGuide =
        """
        Redacta una skill del estándar Agent Skills:
        - name: 1-64 caracteres en minúsculas, números y guiones (sin guiones dobles ni al borde), en forma verbo-sustantivo.
        - description: 1-1024 caracteres, en tercera persona: qué hace y CUÁNDO usarla ("USE FOR: ...") y cuándo no
          ("DO NOT USE FOR: ..."), con las palabras que usaría un usuario. Sin etiquetas XML.
        - instructions (Markdown, idealmente 200-2500 tokens): título, propósito, entradas, flujo de trabajo en pasos
          numerados, ejemplos concretos en bloques de código, criterios de validación y errores comunes. Escribe
          instrucciones accionables, no teoría; incluye solo lo que un modelo capaz no sabría por sí mismo.
        - references: material extenso o de consulta ocasional en archivos aparte (references/x.md), enlazados desde
          las instrucciones con rutas relativas de un nivel.
        """;

    private sealed record Draft(
        [property: Description("Nombre en kebab-case.")] string Name,
        [property: Description("Descripción con qué hace, USE FOR y DO NOT USE FOR.")] string Description,
        [property: Description("Instrucciones en Markdown.")] string Instructions,
        [property: Description("Archivos de referencia opcionales.")] IReadOnlyList<DraftFile>? References = null);

    private sealed record DraftFile(
        [property: Description("Ruta, p. ej. references/ejemplos.md.")] string Path,
        [property: Description("Contenido Markdown.")] string Content);

    /// <summary>
    /// Drafts a skill from a goal and, optionally, reference material (procedures,
    /// examples, internal documentation). Retries with corrections if the result does not pass validation.
    /// </summary>
    public static async Task<Skill> GenerateAsync(
        ILanguageModel model,
        string goal,
        string? referenceMaterial = null,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        string? feedback = null;
        for (var attempt = 1; ; attempt++)
        {
            var prompt = new StringBuilder($"Objetivo de la skill:\n{goal}");
            if (referenceMaterial is not null) prompt.Append("\n\nMaterial de referencia:\n").Append(referenceMaterial);
            if (feedback is not null) prompt.Append("\n\nTu borrador anterior no pasó la validación. Corrige:\n").Append(feedback);

            var draft = (await Ai.GenerateObjectAsync<Draft>(new GenerateObjectOptions
            {
                Model = model,
                System = AuthoringGuide,
                Prompt = prompt.ToString(),
                CancellationToken = cancellationToken,
            }).ConfigureAwait(false)).Object;

            var skill = new Skill
            {
                Name = draft.Name.Trim(),
                Description = draft.Description.Trim(),
                Instructions = draft.Instructions.Trim(),
                Files = SkillFiles.FromText((draft.References ?? []).ToDictionary(r => r.Path, r => r.Content)),
            };
            var validation = skill.Validate();
            if (validation.IsValid || attempt >= maxAttempts)
            {
                validation.ThrowIfInvalid(skill.Name);
                return skill;
            }

            feedback = string.Join("\n", validation.Errors);
        }
    }
}

/// <summary>Options for <see cref="SkillAuthoringTools"/>.</summary>
public sealed class SkillAuthoringOptions
{
    /// <summary>
    /// Gets or sets the folder where created skills are saved (if null, they are only added to the in-memory library).
    /// Skills are always saved with the <see cref="SkillLibrary.ProvenanceFileName"/> marker: when reloaded they are
    /// untrusted even if the folder is loaded as trusted (e.g. with <c>AddSkills(dir)</c>).
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// Gets or sets whether existing skills can be overwritten (in the library or in <see cref="Directory"/>).
    /// </summary>
    public bool AllowOverwrite { get; init; }

    /// <summary>
    /// Gets or sets whether the in-memory copy of skills created by the agent is marked as trusted (their scripts could
    /// run). Not by default. It does not affect the copy on disk, which is always marked as untrusted.
    /// </summary>
    public bool TrustCreatedSkills { get; init; }

    /// <summary>
    /// Gets or sets whether <c>create_skill</c> can include scripts (files in <c>scripts/</c> or with executable
    /// extensions such as <c>.py</c>, <c>.sh</c> or <c>.js</c>). Not by default: an agent manipulated by a prompt
    /// injection should not be able to leave code saved. Even when allowed, the saved skill is marked as untrusted.
    /// </summary>
    public bool AllowScripts { get; init; }
}

/// <summary>
/// Tools that let an agent create skills (like Claude's "skill-creator"): turns a successful
/// workflow into a reusable skill, validates it and registers it.
/// </summary>
public static class SkillAuthoringTools
{
    public const string Instructions =
        "Puedes crear skills reutilizables con create_skill cuando el usuario lo pida o cuando un procedimiento " +
        "repetible merezca guardarse. " + SkillGenerator.AuthoringGuide;

    public static IReadOnlyList<Tool> Create(SkillLibrary library, SkillAuthoringOptions? options = null)
    {
        options ??= new SkillAuthoringOptions();
        var o = options;
        return
        [
            Tool.Create<CreateInput, object>(
                "create_skill",
                "Crea (o actualiza) una skill con instrucciones y archivos de referencia, validada contra el estándar Agent Skills.",
                async (input, _, ct) =>
                {
                    if (library.Contains(input.Name) && !o.AllowOverwrite)
                    {
                        throw new InvalidOperationException($"Ya existe la skill '{input.Name}'.");
                    }

                    var fileErrors = CheckFiles(input.Files ?? [], o);
                    if (fileErrors.Count > 0)
                    {
                        return new { created = false, errors = (IReadOnlyList<string>)fileErrors, warnings = (IReadOnlyList<string>)[] };
                    }

                    var skill = new Skill
                    {
                        Name = input.Name,
                        Description = input.Description,
                        Instructions = input.Instructions,
                        Files = SkillFiles.FromText((input.Files ?? []).ToDictionary(f => f.Path, f => f.Content)),
                        Trusted = o.TrustCreatedSkills,
                    };
                    var validation = skill.Validate();
                    if (!validation.IsValid)
                    {
                        return new { created = false, errors = validation.Errors, warnings = validation.Warnings };
                    }

                    string? location = null;
                    if (o.Directory is { } directory)
                    {
                        if (!o.AllowOverwrite && System.IO.Directory.Exists(Path.Combine(directory, skill.Name)))
                        {
                            throw new InvalidOperationException($"Ya existe la carpeta de la skill '{skill.Name}' en '{directory}'.");
                        }

                        // En disco queda siempre como no confiable (marcador de procedencia), con independencia de
                        // TrustCreatedSkills: una skill escrita por el modelo no se vuelve de confianza al recargarla.
                        location = await SkillLibrary.SaveAsync(skill, directory, "create_skill", ct).ConfigureAwait(false);
                    }

                    library.Add(skill with { Location = location });
                    return new { created = true, name = skill.Name, location, warnings = validation.Warnings };
                }),
            Tool.Create<ValidateInput, object>(
                "validate_skill",
                "Valida el contenido de un SKILL.md sin guardarlo.",
                input =>
                {
                    try
                    {
                        var validation = Skill.Parse(input.SkillMarkdown).Validate();
                        return new { valid = validation.IsValid, errors = validation.Errors, warnings = validation.Warnings };
                    }
                    catch (FormatException ex)
                    {
                        return (object)new { valid = false, errors = new[] { ex.Message }, warnings = Array.Empty<string>() };
                    }
                }),
        ];
    }

    /// <summary>Extensions considered executable code (in addition to everything in <c>scripts/</c>).</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".sh", ".bash", ".zsh", ".fish", ".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".ps1", ".psm1", ".psd1",
        ".bat", ".cmd", ".com", ".exe", ".dll", ".so", ".dylib", ".rb", ".pl", ".php", ".lua", ".jar", ".vbs", ".wsf", ".msi", ".csx", ".cs",
    };

    /// <summary>Validates the file paths: no hidden files or SKILL.md, and no scripts unless they are allowed.</summary>
    private static List<string> CheckFiles(IReadOnlyList<FileInput> files, SkillAuthoringOptions options)
    {
        var errors = new List<string>();
        foreach (var file in files)
        {
            string path;
            try
            {
                path = SkillFiles.NormalizePath(file.Path);
            }
            catch (ArgumentException ex)
            {
                errors.Add(ex.Message);
                continue;
            }

            if (path.Split('/').Any(s => s.StartsWith('.')) || string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Ruta no permitida: '{path}' (archivos ocultos o SKILL.md).");
            }
            else if (!options.AllowScripts && (path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) || ExecutableExtensions.Contains(Path.GetExtension(path))))
            {
                errors.Add($"No se permite crear scripts ('{path}'): las skills creadas por el agente solo pueden tener instrucciones y archivos de referencia.");
            }
        }

        return errors;
    }

    private sealed record FileInput(
        [property: Description("Ruta relativa, p. ej. references/guia.md o assets/plantilla.html.")] string Path,
        [property: Description("Contenido del archivo.")] string Content);

    private sealed record CreateInput(
        [property: Description("Nombre en kebab-case.")] string Name,
        [property: Description("Qué hace y cuándo usarla (máx. 1024 caracteres).")] string Description,
        [property: Description("Instrucciones en Markdown.")] string Instructions,
        [property: Description("Archivos adicionales.")] IReadOnlyList<FileInput>? Files = null);

    private sealed record ValidateInput([property: Description("Contenido completo de SKILL.md.")] string SkillMarkdown);
}
