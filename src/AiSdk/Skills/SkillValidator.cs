using System.Text.RegularExpressions;
using AiSdk.Resources;
using System.Globalization;

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
            throw new FormatException(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_Invalid, skillName, string.Join("\n- ", Errors)));
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
            errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_NameFolderMismatch, skill.Name, directoryName));
        }

        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            errors.Add(Strings.SkillValidation_MissingDescription);
        }
        else if (skill.Description.Length > MaxDescriptionLength)
        {
            errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_DescriptionTooLong, skill.Description.Length, MaxDescriptionLength));
        }
        else if (skill.Description.Length < MinDescriptionLength)
        {
            errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_DescriptionTooShort, skill.Description.Length, MinDescriptionLength));
        }

        if (XmlTag().IsMatch(skill.Description))
        {
            errors.Add(Strings.SkillValidation_DescriptionXmlTags);
        }

        if (skill.Compatibility is { } compatibility && (compatibility.Trim().Length == 0 || compatibility.Length > MaxCompatibilityLength))
        {
            errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_CompatibilityLength, MaxCompatibilityLength));
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
                errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_ReferenceOutside, path));
            }
            else if (path.TrimStart('.', '/').Split('/').Length > 2)
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_ReferenceTooDeep, path));
            }
            else if (skill.Files != SkillFiles.Empty && !skill.Files.List().Contains(path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path))
            {
                warnings.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_ReferenceMissing, path));
            }
        }

        var tokens = TokenEstimator.Estimate(skill.Instructions);
        if (tokens > RecommendedMaxTokens)
        {
            warnings.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_InstructionsTooLong, tokens));
        }
        else if (tokens < 200)
        {
            warnings.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_InstructionsTooShort, tokens));
        }

        if (!skill.Instructions.Contains("\n#", StringComparison.Ordinal) && !skill.Instructions.StartsWith('#'))
        {
            warnings.Add(Strings.SkillValidation_NoHeadings);
        }

        if (!NumberedStep().IsMatch(skill.Instructions))
        {
            warnings.Add(Strings.SkillValidation_NoNumberedSteps);
        }

        if (skill.Description.Length >= MinDescriptionLength && !UsageHint().IsMatch(skill.Description))
        {
            warnings.Add(Strings.SkillValidation_NoWhenToUse);
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
            errors.Add(Strings.SkillValidation_MissingName);
            return;
        }

        if (name.Length > MaxNameLength) errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_NameTooLong, name, MaxNameLength));
        if (!NameFormat().IsMatch(name)) errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_NameFormat, name));
        if (name.StartsWith('-') || name.EndsWith('-')) errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_NameHyphenEdge, name));
        if (name.Contains("--", StringComparison.Ordinal)) errors.Add(string.Format(CultureInfo.CurrentCulture, Strings.SkillValidation_NameConsecutiveHyphens, name));
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
