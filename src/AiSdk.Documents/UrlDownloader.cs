using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiSdk.Documents.Resources;

namespace AiSdk.Documents;

/// <summary>
/// Options for <see cref="UrlDownloader"/>: URL attachments can be chosen by whoever writes the message, and their
/// content is shown to the model, so by default only public http/https URLs are downloaded, without following
/// redirects to private networks, with bounded size and time.
/// </summary>
public sealed class UrlDownloadOptions
{
    /// <summary>Maximum bytes of a download (25 MB by default; never more than <see cref="MarkdownConverterOptions.MaxInputBytes"/>). Checked while reading.</summary>
    public long MaxBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Maximum time for the whole download, including redirects (30 s by default).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum redirects (5 by default). Each target is validated the same way as the original URL.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>
    /// Allows downloading from loopback, private networks (RFC 1918), link-local (including 169.254.169.254, the cloud
    /// metadata endpoint), CGNAT, IPv6 unique local addresses, multicast and other reserved ranges. Enable it only if
    /// the URLs come from a trusted source: with it, whoever controls the URL can read internal services.
    /// </summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>
    /// Additional validation of each URL, including redirect targets (e.g. an allowlist of domains). If it returns false,
    /// the download is rejected.
    /// </summary>
    public Func<Uri, bool>? UrlValidator { get; init; }

    /// <summary>
    /// Decides which IP addresses can be connected to, instead of the default rule (<see cref="UrlDownloader.IsPublicAddress"/>,
    /// or any address with <see cref="AllowPrivateNetworks"/>). Evaluated with each resolved address, when connecting.
    /// </summary>
    public Func<IPAddress, bool>? AddressValidator { get; init; }
}

/// <summary>Rejected download (disallowed scheme, address or URL, too many redirects, HTTP error or timeout).</summary>
public sealed class UrlDownloadException : AiSdkException
{
    public UrlDownloadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Downloaded content: bytes, MIME type reported by the server (or the <c>data:</c> URL) and final URL.</summary>
public sealed record UrlDownloadResult(byte[] Data, string? MediaType, Uri Url);

/// <summary>
/// Downloads with SSRF protection: http/https only (and <c>data:</c> URLs), direct connection (no proxy) to the already
/// validated IP address (so a DNS record that changes between validation and connection cannot be used to reach an
/// internal network), manual redirects validated at each hop, maximum size checked while reading, and a maximum time.
/// </summary>
public static class UrlDownloader
{
    /// <summary>Downloads <paramref name="url"/> with the protections of <paramref name="options"/>.</summary>
    /// <exception cref="UrlDownloadException">Disallowed URL, scheme or address, HTTP error, too many redirects or timeout.</exception>
    /// <exception cref="DocumentTooLargeException">The content exceeds <see cref="UrlDownloadOptions.MaxBytes"/>.</exception>
    public static Task<UrlDownloadResult> DownloadAsync(Uri url, UrlDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new UrlDownloadOptions();
        return DownloadAsync(url, options, options.MaxBytes, cancellationToken);
    }

    internal static async Task<UrlDownloadResult> DownloadAsync(Uri url, UrlDownloadOptions options, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        if (url.IsAbsoluteUri && url.Scheme == "data")
        {
            return DecodeDataUrl(url, maxBytes);
        }

        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var handler = CreateHandler(options);
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        try
        {
            var current = url;
            for (var redirects = 0; ; redirects++)
            {
                Validate(current, options);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.InnerException is UrlDownloadException blocked)
                {
                    throw new UrlDownloadException(blocked.Message, ex);
                }

                using (response)
                {
                    if (IsRedirect(response.StatusCode))
                    {
                        if (response.Headers.Location is not { } location)
                        {
                            throw new UrlDownloadException(string.Format(CultureInfo.CurrentCulture, Strings.Download_RedirectWithoutLocation, Describe(current), (int)response.StatusCode));
                        }

                        if (redirects >= options.MaxRedirects)
                        {
                            throw new UrlDownloadException(string.Format(CultureInfo.CurrentCulture, Strings.Download_TooManyRedirects, Describe(url), options.MaxRedirects));
                        }

                        current = location.IsAbsoluteUri ? location : new Uri(current, location);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new UrlDownloadException(string.Format(CultureInfo.CurrentCulture, Strings.Download_HttpError, Describe(current), (int)response.StatusCode, response.ReasonPhrase));
                    }

                    var name = Path.GetFileName(current.LocalPath);
                    if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
                    {
                        throw new DocumentTooLargeException(length, maxBytes, name);
                    }

                    var data = await ReadLimitedAsync(response.Content, maxBytes, name, linked.Token).ConfigureAwait(false);
                    return new UrlDownloadResult(data, response.Content.Headers.ContentType?.MediaType, current);
                }
            }
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new UrlDownloadException(string.Format(CultureInfo.CurrentCulture, Strings.Download_Timeout, Describe(url), options.Timeout.TotalSeconds), ex);
        }
    }

    /// <summary>
    /// True if the address is public (global unicast). Rejects loopback, "this network" (0.0.0.0/8), RFC 1918, CGNAT
    /// (100.64.0.0/10), link-local (169.254.0.0/16 and fe80::/10), documentation and test networks, multicast,
    /// reserved ranges, IPv6 unique local addresses (fc00::/7), and IPv6 addresses that embed an IPv4 address (mapped,
    /// NAT64, 6to4, Teredo) when the IPv4 address is not public.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out var written))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork && written == 4)
        {
            return IsPublicIPv4(b[..4]);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || written != 16)
        {
            return false;
        }

        // NAT64 (64:ff9b::/96): la IPv4 va en los últimos 4 bytes. 64:ff9b:1::/48 es de uso local.
        if (b[..12].SequenceEqual((ReadOnlySpan<byte>)[0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0]))
        {
            return IsPublicIPv4(b[12..]);
        }

        // Solo unicast global (2000::/3); el resto (::, ::1, ::/96, fc00::/7, fe80::/10, ff00::/8...) no es público.
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        var isIetf = b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0; // 2001::/23 (Teredo, pruebas, ORCHID)
        var isDocumentation = (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // 2001:db8::/32
                              || (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xF0) == 0); // 3fff::/20
        var is6To4 = b[0] == 0x20 && b[1] == 0x02; // 2002::/16
        return !isIetf && !isDocumentation && !is6To4;
    }

    private static bool IsPublicIPv4(ReadOnlySpan<byte> b) =>
        !(b[0] == 0 // "esta red"
          || b[0] == 10
          || b[0] == 127
          || (b[0] == 100 && (b[1] & 0xC0) == 64) // CGNAT 100.64.0.0/10
          || (b[0] == 169 && b[1] == 254) // link-local y metadatos de la nube
          || (b[0] == 172 && (b[1] & 0xF0) == 16)
          || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // asignaciones IETF y TEST-NET-1
          || (b[0] == 192 && b[1] == 88 && b[2] == 99) // relé 6to4
          || (b[0] == 192 && b[1] == 168)
          || (b[0] == 198 && (b[1] & 0xFE) == 18) // pruebas de rendimiento
          || (b[0] == 198 && b[1] == 51 && b[2] == 100) // TEST-NET-2
          || (b[0] == 203 && b[1] == 0 && b[2] == 113) // TEST-NET-3
          || b[0] >= 224); // multicast, reservadas y difusión

    private static bool IsAllowed(IPAddress address, UrlDownloadOptions options) =>
        options.AddressValidator?.Invoke(address) ?? (options.AllowPrivateNetworks || IsPublicAddress(address));

    private static void Validate(Uri url, UrlDownloadOptions options)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            throw new UrlDownloadException(string.Format(CultureInfo.CurrentCulture, Strings.Download_OnlyHttp, Describe(url)));
        }

        if (options.UrlValidator is { } validator && !validator(url))
        {
            throw new UrlDownloadException(string.Format(CultureInfo.CurrentCulture, Strings.Download_UrlNotAllowed, Describe(url)));
        }

        // Una IP literal se rechaza antes de conectar (el ConnectCallback valida igual cada conexión).
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal) && !IsAllowed(literal, options))
        {
            throw new UrlDownloadException(BlockedMessage(url.Host, [literal]));
        }
    }

    private static SocketsHttpHandler CreateHandler(UrlDownloadOptions options) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false, // con un proxy se validaría la IP del proxy, no la del destino
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = options.Timeout,
        MaxResponseHeadersLength = 64,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host.Trim('[', ']');
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var allowed = addresses.Where(a => IsAllowed(a, options)).ToList();
            if (allowed.Count == 0)
            {
                throw new UrlDownloadException(BlockedMessage(host, addresses));
            }

            SocketException? lastError = null;
            foreach (var address in allowed)
            {
                // Se conecta a la dirección ya validada, no al nombre (que podría resolverse distinto).
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    lastError = ex;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new HttpRequestException(string.Format(CultureInfo.CurrentCulture, Strings.Download_CouldNotConnect, host), lastError);
        },
    };

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, string? name, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new DocumentTooLargeException(buffer.Length + read, maxBytes, name);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary><c>data:[type][;base64],data</c> URL, checking the size before decoding.</summary>
    private static UrlDownloadResult DecodeDataUrl(Uri url, long maxBytes)
    {
        var raw = url.OriginalString;
        var comma = raw.IndexOf(',', StringComparison.Ordinal);
        if (comma < 5)
        {
            throw new UrlDownloadException(Strings.Download_DataUrlMissingComma);
        }

        var meta = raw[5..comma];
        var payload = raw[(comma + 1)..];
        var isBase64 = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var size = isBase64 ? (long)payload.Length * 3 / 4 : payload.Length;
        if (size > maxBytes)
        {
            throw new DocumentTooLargeException(size, maxBytes);
        }

        try
        {
            var data = isBase64 ? Convert.FromBase64String(payload) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            var mediaType = (isBase64 ? meta[..^7] : meta).Split(';')[0].Trim();
            return new UrlDownloadResult(data, mediaType.Length == 0 ? null : mediaType, url);
        }
        catch (FormatException ex)
        {
            throw new UrlDownloadException(Strings.Download_DataUrlInvalidBase64, ex);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.MultipleChoices;

    private static string BlockedMessage(string host, IReadOnlyCollection<IPAddress> addresses) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Download_Blocked, host, string.Join(", ", addresses));

    /// <summary>URL without credentials or query, for error messages.</summary>
    private static string Describe(Uri url) =>
        url.IsAbsoluteUri ? $"'{url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped)}'" : $"'{url}'";
}
