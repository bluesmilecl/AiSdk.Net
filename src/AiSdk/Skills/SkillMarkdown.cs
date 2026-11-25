using System.Globalization;
using System.Text;

namespace AiSdk.Skills;

/// <summary>
/// Reading and writing of <c>SKILL.md</c>: YAML frontmatter (the subset used by the Agent Skills
/// standard: strings, <c>&gt;</c>/<c>|</c> blocks, lists and one level of maps) and a Markdown body.
/// </summary>
public static class SkillMarkdown
{
    /// <summary>Separates the frontmatter from the body. Returns a null frontmatter if the file has none.</summary>
    public static (IReadOnlyDictionary<string, object?>? Frontmatter, string Body) Parse(string content)
    {
        content = content.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (null, content.Trim());
        }

        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("El frontmatter de SKILL.md no está cerrado con '---'.");
        }

        var yaml = content[4..(end + 1)];
        var bodyStart = content.IndexOf('\n', end + 4);
        var body = bodyStart < 0 ? string.Empty : content[(bodyStart + 1)..];
        return (ParseYaml(yaml), body.Trim());
    }

    /// <summary>Minimal YAML parser for frontmatter (one-level maps, lists, scalars and blocks).</summary>
    public static IReadOnlyDictionary<string, object?> ParseYaml(string yaml)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsBlankOrComment(line) || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FormatException($"Línea de frontmatter inválida: '{line}'.");
            }

            var key = line[..colon].Trim();
            var rest = StripComment(line[(colon + 1)..]).Trim();

            if (rest is ">" or ">-" or ">+" or "|" or "|-" or "|+")
            {
                var block = CollectIndented(lines, ref i);
                result[key] = rest[0] == '>' ? Fold(block) : string.Join('\n', block).TrimEnd();
            }
            else if (rest.Length == 0)
            {
                // Mapa o lista anidada (o valor vacío). Las líneas en blanco y los comentarios del bloque se ignoran.
                var block = CollectIndented(lines, ref i).Where(b => !IsBlankOrComment(b)).ToList();
                if (block.Count == 0)
                {
                    result[key] = null;
                }
                else if (block[0].TrimStart().StartsWith("- ", StringComparison.Ordinal) || block[0].Trim() == "-")
                {
                    if (block.Any(b => !b.TrimStart().StartsWith('-')))
                    {
                        throw new FormatException($"Lista inválida en el frontmatter ('{key}'): todas las líneas deben empezar con '-'.");
                    }

                    result[key] = block.Select(b => Scalar(StripComment(b.TrimStart()[1..]).Trim())).ToList();
                }
                else
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in block)
                    {
                        var c = entry.IndexOf(':', StringComparison.Ordinal);
                        if (c > 0)
                        {
                            map[entry[..c].Trim()] = Scalar(StripComment(entry[(c + 1)..]).Trim());
                        }
                    }

                    result[key] = map;
                }
            }
            else if (rest.StartsWith('[') && rest.EndsWith(']'))
            {
                result[key] = rest[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Scalar).ToList();
            }
            else
            {
                // Escalar, posiblemente continuado en líneas más indentadas (plain multi-line).
                var value = Scalar(rest);
                if (rest[0] is not ('"' or '\''))
                {
                    var continuation = CollectIndented(lines, ref i);
                    if (continuation.Count > 0)
                    {
                        value = value + " " + Fold(continuation);
                    }
                }

                result[key] = value;
            }
        }

        return result;
    }

    private static bool IsBlankOrComment(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    private static string StripComment(string value)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '\'' when !inDouble: inSingle = !inSingle; break;
                case '"' when !inSingle: inDouble = !inDouble; break;
                case '#' when !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(value[i - 1])): return value[..i];
            }
        }

        return value;
    }

    /// <summary>Following lines indented more than the key (without the common indentation).</summary>
    private static List<string> CollectIndented(string[] lines, ref int index)
    {
        var block = new List<string>();
        while (index + 1 < lines.Length && (lines[index + 1].Trim().Length == 0 || char.IsWhiteSpace(lines[index + 1][0])))
        {
            block.Add(lines[++index]);
        }

        while (block.Count > 0 && block[^1].Trim().Length == 0)
        {
            block.RemoveAt(block.Count - 1);
        }

        var indent = block.Where(b => b.Trim().Length > 0).Select(b => b.Length - b.TrimStart().Length).DefaultIfEmpty(0).Min();
        return block.Select(b => b.Length >= indent ? b[indent..] : b.TrimStart()).ToList();
    }

    /// <summary>YAML folding: lines are joined with spaces and empty lines are paragraph breaks.</summary>
    private static string Fold(List<string> block)
    {
        var builder = new StringBuilder();
        foreach (var line in block)
        {
            if (line.Trim().Length == 0)
            {
                builder.Append('\n');
            }
            else
            {
                if (builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.Append(' ');
                }

                builder.Append(line.Trim());
            }
        }

        return builder.ToString().Trim();
    }

    private static string Scalar(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }

    /// <summary>Generates the content of <c>SKILL.md</c> (frontmatter + body).</summary>
    public static string Write(Skill skill)
    {
        var builder = new StringBuilder("---\n");
        builder.Append("name: ").Append(skill.Name).Append('\n');
        AppendFolded(builder, "description", skill.Description);
        if (skill.License is { } license) builder.Append("license: ").Append(Quote(license)).Append('\n');
        if (skill.Compatibility is { } compatibility) AppendFolded(builder, "compatibility", compatibility);
        if (skill.AllowedTools.Count > 0) builder.Append("allowed-tools: ").Append(Quote(string.Join(' ', skill.AllowedTools))).Append('\n');
        if (skill.Metadata.Count > 0)
        {
            builder.Append("metadata:\n");
            foreach (var (key, value) in skill.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                builder.Append("  ").Append(key).Append(": ").Append(Quote(value)).Append('\n');
            }
        }

        foreach (var (key, value) in skill.Extra.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            builder.Append(key).Append(": ").Append(Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)).Append('\n');
        }

        builder.Append("---\n\n").Append(skill.Instructions.Trim()).Append('\n');
        return builder.ToString();
    }

    private static void AppendFolded(StringBuilder builder, string key, string value)
    {
        if (value.Length <= 80 && !value.Contains('\n', StringComparison.Ordinal))
        {
            builder.Append(key).Append(": ").Append(Quote(value)).Append('\n');
            return;
        }

        if (value.Contains('\n', StringComparison.Ordinal))
        {
            // Bloque literal: conserva los saltos de línea exactamente.
            builder.Append(key).Append(": |-\n");
            foreach (var line in value.Split('\n'))
            {
                builder.Append(line.Length == 0 ? string.Empty : "  " + line).Append('\n');
            }

            return;
        }

        builder.Append(key).Append(": >-\n");
        foreach (var paragraph in value.Split('\n'))
        {
            var line = new StringBuilder("  ");
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + word.Length > 100 && line.Length > 2)
                {
                    builder.Append(line.ToString().TrimEnd()).Append('\n');
                    line.Clear().Append("  ");
                }

                line.Append(word).Append(' ');
            }

            builder.Append(line.ToString().TrimEnd()).Append('\n');
        }
    }

    /// <summary>Quotes the value if it contains YAML special characters.</summary>
    private static string Quote(string value) =>
        value.Length == 0 || value.IndexOfAny([':', '#', '\'', '"', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`']) >= 0
        || value.StartsWith(' ') || value.EndsWith(' ') || value is "true" or "false" or "null" or "yes" or "no"
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
}
