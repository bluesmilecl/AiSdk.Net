using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiSdk.Internal;

/// <summary>Utilidades JSON compartidas por el núcleo y los proveedores.</summary>
public static class JsonUtil
{
    public static JsonElement EmptyObject { get; } = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Parsea JSON; si falla devuelve <c>false</c> y el texto como string JSON.</summary>
    public static bool TryParseElement(string? text, out JsonElement element)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            element = EmptyObject;
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = JsonSerializer.SerializeToElement(text);
            return false;
        }
    }

    /// <summary>Fusión profunda: las propiedades de <paramref name="source"/> sobrescriben las de <paramref name="target"/>.</summary>
    public static void DeepMerge(JsonObject target, JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObj && target[key] is JsonObject targetObj)
            {
                DeepMerge(targetObj, sourceObj);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    public static JsonNode? ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText());

    /// <summary>
    /// Intenta parsear JSON incompleto (por ejemplo, mientras se recibe por streaming),
    /// cerrando strings, arrays y objetos abiertos. Devuelve null si no es posible.
    /// </summary>
    public static JsonNode? ParsePartial(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return null;
        }

        text = text[start..];
        for (var length = text.Length; length > 0; length--)
        {
            var candidate = CloseJson(text.AsSpan(0, length));
            if (candidate is null)
            {
                continue;
            }

            try
            {
                return JsonNode.Parse(candidate);
            }
            catch (JsonException)
            {
                // Probar con un prefijo más corto.
            }
        }

        return null;
    }

    private static string? CloseJson(ReadOnlySpan<char> text)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var c in text)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    stack.Push('}');
                    break;
                case '[':
                    stack.Push(']');
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0 || stack.Pop() != c)
                    {
                        return null;
                    }

                    break;
            }
        }

        if (escaped)
        {
            return null;
        }

        var sb = new StringBuilder(text.Length + stack.Count + 1);
        sb.Append(text);
        if (inString)
        {
            sb.Append('"');
        }

        var trimmed = sb.ToString().TrimEnd();
        if (trimmed.EndsWith(',') || trimmed.EndsWith(':'))
        {
            return null;
        }

        sb.Clear().Append(trimmed);
        while (stack.Count > 0)
        {
            sb.Append(stack.Pop());
        }

        return sb.ToString();
    }
}
