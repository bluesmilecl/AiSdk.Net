using System.Text.RegularExpressions;

namespace AiSdk.Skills;

/// <summary>Result of validating a skill.</summary>
public sealed record SkillValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>Throws <see cref="FormatException"/> with all the errors if the skill is not valid.</summary>
    public void ThrowIfInvalid(string skillName)
    {
        if (!IsValid)
        {
            throw new FormatException($"La skill '{skillName}' no es válida:\n- {string.Join("\n- ", Errors)}");
        }
    }
}

/// <summary>
/// Validates skills against the <see href="https://agentskills.io/specification">agentskills.io</see> specification
/// (errors) and authoring best practices (warnings), with the same thresholds as the
/// <c>dotnet/skills</c> validator.
/// </summary>
public static partial class SkillValidator
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int MinDescriptionLength = 10;
    public const int MaxCompatibilityLength = 500;

    /// <summary>
    /// Recommended size of the instructions (tokens): above it, it is better to split into several skills.
    /// </summary>
    public const int RecommendedMaxTokens = 5000;

    public static SkillValidationResult Validate(Skill skill, string? directoryName = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateName(skill.Name, errors);
        if (directoryName is not null && directoryName != skill.Name)
        {
            errors.Add($"El nombre '{skill.Name}' no coincide con la carpeta '{directoryName}'.");
        }

        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            errors.Add("Falta la descripción: el modelo la usa para decidir cuándo activar la skill.");
        }
        else if (skill.Description.Length > MaxDescriptionLength)
        {
            errors.Add($"La descripción tiene {skill.Description.Length} caracteres; el máximo es {MaxDescriptionLength}.");
        }
        else if (skill.Description.Length < MinDescriptionLength)
        {
            errors.Add($"La descripción tiene {skill.Description.Length} caracteres; el mínimo es {MinDescriptionLength}.");
        }

        if (XmlTag().IsMatch(skill.Description))
        {
            errors.Add("La descripción no puede contener etiquetas XML (p. ej. <tag>); Claude las rechaza.");
        }

        if (skill.Compatibility is { } compatibility && (compatibility.Trim().Length == 0 || compatibility.Length > MaxCompatibilityLength))
        {
            errors.Add($"'compatibility' debe tener entre 1 y {MaxCompatibilityLength} caracteres.");
        }

        foreach (Match reference in FileReference().Matches(skill.Instructions))
        {
            var path = reference.Groups[1].Value.Split('#')[0];
            if (path.Length == 0 || path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || path.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (path.StartsWith('/') || path.Split('/').Contains(".."))
            {
                errors.Add($"La referencia '{path}' sale de la carpeta de la skill; use rutas relativas.");
            }
            else if (path.TrimStart('.', '/').Split('/').Length > 2)
            {
                errors.Add($"La referencia '{path}' tiene más de un nivel de carpetas desde SKILL.md.");
            }
            else if (skill.Files != SkillFiles.Empty && !skill.Files.List().Contains(path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path))
            {
                warnings.Add($"La referencia '{path}' no existe en los archivos de la skill.");
            }
        }

        var tokens = TokenEstimator.Estimate(skill.Instructions);
        if (tokens > RecommendedMaxTokens)
        {
            warnings.Add($"Las instrucciones tienen ~{tokens} tokens; las skills muy largas rinden peor. Divídala o mueva detalle a references/.");
        }
        else if (tokens < 200)
        {
            warnings.Add($"Las instrucciones tienen ~{tokens} tokens; probablemente son demasiado escuetas para guiar al modelo.");
        }

        if (!skill.Instructions.Contains("\n#", StringComparison.Ordinal) && !skill.Instructions.StartsWith('#'))
        {
            warnings.Add("Sin títulos de sección: los modelos navegan mejor documentos estructurados.");
        }

        if (!NumberedStep().IsMatch(skill.Instructions))
        {
            warnings.Add("Sin pasos numerados: los modelos siguen mejor procedimientos secuenciados.");
        }

        if (skill.Description.Length >= MinDescriptionLength && !UsageHint().IsMatch(skill.Description))
        {
            warnings.Add("La descripción no indica cuándo usarla (p. ej. \"Use cuando...\" o \"USE FOR:\"); eso mejora la activación.");
        }

        return new SkillValidationResult(errors, warnings);
    }

    /// <summary>
    /// Rules for the <c>name</c> field: 1-64 characters, lowercase letters, digits and hyphens, no leading, trailing or
    /// double hyphens.
    /// </summary>
    public static void ValidateName(string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Falta el nombre (1-64 caracteres en minúsculas, números y guiones).");
            return;
        }

        if (name.Length > MaxNameLength) errors.Add($"El nombre '{name}' supera {MaxNameLength} caracteres.");
        if (!NameFormat().IsMatch(name)) errors.Add($"El nombre '{name}' solo puede tener minúsculas, números y guiones.");
        if (name.StartsWith('-') || name.EndsWith('-')) errors.Add($"El nombre '{name}' no puede empezar ni terminar con guion.");
        if (name.Contains("--", StringComparison.Ordinal)) errors.Add($"El nombre '{name}' no puede tener guiones consecutivos.");
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameFormat();

    [GeneratedRegex(@"<[A-Za-z/][^>]*>")]
    private static partial Regex XmlTag();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex FileReference();

    [GeneratedRegex(@"(?m)^\s*\d+\.\s")]
    private static partial Regex NumberedStep();

    [GeneratedRegex(@"(?i)\b(use (when|for|this)|usar? (cuando|para)|úsala|usala|when\b|cuando\b)")]
    private static partial Regex UsageHint();
}
