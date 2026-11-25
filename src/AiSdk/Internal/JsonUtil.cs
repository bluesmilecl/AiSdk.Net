using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiSdk.Internal;

/// <summary>Utilidades JSON compartidas por el núcleo y los proveedores.</summary>
public static class JsonUtil
{
    public static JsonElement EmptyObject { get; } = JsonElement.Parse("{}");

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
    /// <remarks>
    /// Tiempo lineal: un solo recorrido repara el prefijo (como <c>fixJson</c> del AI SDK) y se parsea una vez.
    /// Se llama con el texto acumulado en cada fragmento del stream, así que no puede probar prefijos más cortos.
    /// </remarks>
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

        try
        {
            return JsonNode.Parse(RepairPartialJson(text.AsSpan(start)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private enum PartialFrame : byte
    {
        ObjectStart,
        ObjectKey,
        ObjectAfterKey,
        ObjectBeforeValue,
        ObjectAfterValue,
        ObjectAfterComma,
        ArrayStart,
        ArrayAfterValue,
        ArrayAfterComma,
    }

    private enum PartialToken : byte
    {
        None,
        String,
        Escape,
        Unicode,
        Number,
        Literal,
    }

    /// <summary>
    /// Devuelve el prefijo válido más largo de <paramref name="text"/> (que empieza con <c>{</c> o <c>[</c>) con sus
    /// strings, literales, arrays y objetos cerrados. Las claves incompletas y los valores a medio escribir que no
    /// pueden completarse (p. ej. <c>"a":</c> o <c>1.</c>) se descartan; lo que sigue a un carácter inválido o al
    /// cierre del valor raíz se ignora.
    /// </summary>
    internal static string RepairPartialJson(ReadOnlySpan<char> text)
    {
        var frames = new List<PartialFrame>();
        var token = PartialToken.None;
        var inKey = false;
        var unicodeLeft = 0;
        var literalStart = 0;
        var validLength = 0; // el prefijo text[..validLength] + los cierres es JSON válido

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (token)
            {
                case PartialToken.String:
                    if (c == '"')
                    {
                        token = PartialToken.None;
                        if (inKey)
                        {
                            inKey = false;
                            frames[^1] = PartialFrame.ObjectAfterKey;
                        }
                        else
                        {
                            validLength = i + 1;
                        }
                    }
                    else if (c == '\\')
                    {
                        token = PartialToken.Escape;
                    }
                    else if (!inKey)
                    {
                        validLength = i + 1;
                    }

                    continue;
                case PartialToken.Escape:
                    if (c == 'u')
                    {
                        token = PartialToken.Unicode;
                        unicodeLeft = 4;
                    }
                    else if (c is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't')
                    {
                        token = PartialToken.String;
                        validLength = inKey ? validLength : i + 1;
                    }
                    else
                    {
                        return Close(text, validLength, frames, token, inKey, literalStart);
                    }

                    continue;
                case PartialToken.Unicode:
                    if (!char.IsAsciiHexDigit(c))
                    {
                        return Close(text, validLength, frames, token, inKey, literalStart);
                    }

                    if (--unicodeLeft == 0)
                    {
                        token = PartialToken.String;
                        validLength = inKey ? validLength : i + 1;
                    }

                    continue;
                case PartialToken.Number:
                    if (char.IsAsciiDigit(c))
                    {
                        validLength = i + 1;
                        continue;
                    }

                    if (c is '.' or 'e' or 'E' or '+' or '-')
                    {
                        continue;
                    }

                    token = PartialToken.None; // fin del número: c se procesa como carácter estructural
                    break;
                case PartialToken.Literal:
                    if (IsLiteralPrefix(text[literalStart..(i + 1)]))
                    {
                        validLength = i + 1;
                        continue;
                    }

                    if (!IsLiteral(text[literalStart..i]))
                    {
                        return Close(text, validLength, frames, token, inKey, literalStart);
                    }

                    token = PartialToken.None;
                    break;
            }

            if (c is ' ' or '\t' or '\n' or '\r')
            {
                continue;
            }

            if (frames.Count == 0)
            {
                // Raíz: siempre un objeto o un array (el llamador salta lo anterior a '{' o '[').
                if (!StartValue(c, i))
                {
                    break;
                }

                continue;
            }

            var ok = true;
            switch (frames[^1])
            {
                case PartialFrame.ObjectStart when c == '}':
                case PartialFrame.ObjectAfterValue when c == '}':
                case PartialFrame.ArrayStart when c == ']':
                case PartialFrame.ArrayAfterValue when c == ']':
                    frames.RemoveAt(frames.Count - 1);
                    validLength = i + 1;
                    break;
                case PartialFrame.ObjectStart or PartialFrame.ObjectAfterComma when c == '"':
                    frames[^1] = PartialFrame.ObjectKey;
                    token = PartialToken.String;
                    inKey = true;
                    break;
                case PartialFrame.ObjectAfterKey when c == ':':
                    frames[^1] = PartialFrame.ObjectBeforeValue;
                    break;
                case PartialFrame.ObjectAfterValue when c == ',':
                    frames[^1] = PartialFrame.ObjectAfterComma;
                    break;
                case PartialFrame.ArrayAfterValue when c == ',':
                    frames[^1] = PartialFrame.ArrayAfterComma;
                    break;
                case PartialFrame.ObjectBeforeValue:
                    frames[^1] = PartialFrame.ObjectAfterValue;
                    ok = StartValue(c, i);
                    break;
                case PartialFrame.ArrayStart or PartialFrame.ArrayAfterComma:
                    frames[^1] = PartialFrame.ArrayAfterValue;
                    ok = StartValue(c, i);
                    break;
                default:
                    ok = false;
                    break;
            }

            if (!ok || frames.Count == 0)
            {
                break; // carácter inválido o valor raíz completo: el resto se ignora
            }
        }

        return Close(text, validLength, frames, token, inKey, literalStart);

        // El contenedor padre ya quedó en "después del valor": al cerrar este valor no hay que tocarlo.
        bool StartValue(char c, int i)
        {
            switch (c)
            {
                case '{':
                    frames.Add(PartialFrame.ObjectStart);
                    validLength = i + 1;
                    return true;
                case '[':
                    frames.Add(PartialFrame.ArrayStart);
                    validLength = i + 1;
                    return true;
                case '"':
                    token = PartialToken.String;
                    validLength = i + 1;
                    return true;
                case '-':
                    token = PartialToken.Number;
                    return true;
                case >= '0' and <= '9':
                    token = PartialToken.Number;
                    validLength = i + 1;
                    return true;
                case 't' or 'f' or 'n':
                    token = PartialToken.Literal;
                    literalStart = i;
                    validLength = i + 1;
                    return true;
                default:
                    return false;
            }
        }
    }

    private static bool IsLiteralPrefix(ReadOnlySpan<char> partial) =>
        "true".AsSpan().StartsWith(partial) || "false".AsSpan().StartsWith(partial) || "null".AsSpan().StartsWith(partial);

    private static bool IsLiteral(ReadOnlySpan<char> value) => value is "true" or "false" or "null";

    private static string Close(ReadOnlySpan<char> text, int validLength, List<PartialFrame> frames, PartialToken token, bool inKey, int literalStart)
    {
        var builder = new StringBuilder(validLength + frames.Count + 5);
        builder.Append(text[..validLength]);
        switch (token)
        {
            case PartialToken.String or PartialToken.Escape or PartialToken.Unicode when !inKey:
                builder.Append('"');
                break;
            case PartialToken.Literal when validLength > literalStart:
                var partial = text[literalStart..validLength];
                foreach (var literal in (ReadOnlySpan<string>)["true", "false", "null"])
                {
                    if (literal.AsSpan().StartsWith(partial))
                    {
                        builder.Append(literal.AsSpan(partial.Length));
                        break;
                    }
                }

                break;
        }

        for (var i = frames.Count - 1; i >= 0; i--)
        {
            builder.Append(frames[i] >= PartialFrame.ArrayStart ? ']' : '}');
        }

        return builder.ToString();
    }
}
