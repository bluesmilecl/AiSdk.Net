using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiSdk.Bedrock;

/// <summary>AWS credentials.</summary>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null)
{
    /// <summary>Reads <c>AWS_ACCESS_KEY_ID</c>, <c>AWS_SECRET_ACCESS_KEY</c> and <c>AWS_SESSION_TOKEN</c>.</summary>
    public static AwsCredentials? FromEnvironment()
    {
        var id = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)
            ? null
            : new AwsCredentials(id, secret, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));
    }
}

/// <summary>Request signing with AWS Signature Version 4.</summary>
public static class AwsSigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Signs the request by adding <c>x-amz-date</c>, <c>x-amz-security-token</c> (if any) and
    /// <c>Authorization</c>. <paramref name="body"/> must be exactly the body that will be sent.
    /// </summary>
    public static void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body, string region, string service, AwsCredentials credentials, DateTimeOffset now)
    {
        var uri = request.RequestUri ?? throw new ArgumentException(Resources.Strings.SigV4_RequestWithoutUri);
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-date"] = amzDate,
        };
        if (credentials.SessionToken is { Length: > 0 } token)
        {
            headers["x-amz-security-token"] = token;
        }

        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            CanonicalUri(uri),
            CanonicalQuery(uri),
            string.Concat(headers.Select(h => $"{h.Key}:{h.Value.Trim()}\n")),
            signedHeaders,
            Hex(SHA256.HashData(body)));

        var scope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n', Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (headers.TryGetValue("x-amz-security-token", out var securityToken))
        {
            request.Headers.Remove("x-amz-security-token");
            request.Headers.TryAddWithoutValidation("x-amz-security-token", securityToken);
        }

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>
    /// Canonical path: for services other than S3, each segment is encoded twice
    /// (the path already arrives encoded once, e.g. <c>%3A</c> → <c>%253A</c>).
    /// </summary>
    private static string CanonicalUri(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return string.Join('/', path.Split('/').Select(Encode));
    }

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return string.Empty;
        }

        return string.Join('&', query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var index = pair.IndexOf('=');
                var key = Uri.UnescapeDataString(index < 0 ? pair : pair[..index]);
                var value = index < 0 ? string.Empty : Uri.UnescapeDataString(pair[(index + 1)..]);
                return (Key: Encode(key), Value: Encode(value));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>RFC 3986 encoding (only unreserved characters are left unencoded).</summary>
    private static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
