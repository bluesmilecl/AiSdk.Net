using System.ComponentModel;
using System.Text;

namespace AiSdk.Skills;

/// <summary>Construcción fluida de skills por código.</summary>
/// <example>
/// <code>
/// var skill = new SkillBuilder("revisar-contratos")
///     .WithDescription("Revisa contratos de arriendo chilenos. Úsala cuando el usuario adjunte un contrato.")
///     .WithInstructions("# Revisión\n\n1. Identifica las partes...")
///     .AddFile("references/clausulas.md", clausulas)
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

    /// <summary>Solo el usuario puede invocarla (<c>disable-model-invocation: true</c>).</summary>
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

    /// <summary>Crea la skill (lanza <see cref="FormatException"/> si no cumple el estándar).</summary>
    public Skill Build(bool trusted = true)
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

/// <summary>Redacta skills con un modelo, siguiendo las buenas prácticas del estándar.</summary>
public static class SkillGenerator
{
    /// <summary>Guía de redacción que se le da al modelo (resumen de agentskills.io y dotnet/skills).</summary>
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
    /// Redacta una skill a partir de un objetivo y, opcionalmente, material de referencia (procedimientos,
    /// ejemplos, documentación interna). Reintenta corrigiendo si el resultado no pasa la validación.
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

/// <summary>Opciones de <see cref="SkillAuthoringTools"/>.</summary>
public sealed class SkillAuthoringOptions
{
    /// <summary>Carpeta donde se guardan las skills creadas (si es null, solo se agregan a la librería en memoria).</summary>
    public string? Directory { get; init; }

    /// <summary>Permite sobrescribir skills existentes.</summary>
    public bool AllowOverwrite { get; init; }

    /// <summary>Si las skills creadas por el agente se marcan de confianza (sus scripts podrían ejecutarse). Por defecto no.</summary>
    public bool TrustCreatedSkills { get; init; }
}

/// <summary>
/// Herramientas para que un agente cree skills (como el "skill-creator" de Claude): convierte un flujo de
/// trabajo exitoso en una skill reutilizable, la valida y la registra.
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
                        location = await SkillLibrary.SaveAsync(skill, directory, ct).ConfigureAwait(false);
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

    private sealed record FileInput(
        [property: Description("Ruta relativa, p. ej. references/guia.md o scripts/procesar.py.")] string Path,
        [property: Description("Contenido del archivo.")] string Content);

    private sealed record CreateInput(
        [property: Description("Nombre en kebab-case.")] string Name,
        [property: Description("Qué hace y cuándo usarla (máx. 1024 caracteres).")] string Description,
        [property: Description("Instrucciones en Markdown.")] string Instructions,
        [property: Description("Archivos adicionales.")] IReadOnlyList<FileInput>? Files = null);

    private sealed record ValidateInput([property: Description("Contenido completo de SKILL.md.")] string SkillMarkdown);
}
