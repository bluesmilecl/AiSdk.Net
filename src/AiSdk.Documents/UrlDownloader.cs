using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AiSdk.Documents;

/// <summary>
/// Opciones de <see cref="UrlDownloader"/>: los adjuntos por URL los puede elegir quien escribe el mensaje, y su
/// contenido se muestra al modelo, así que por defecto solo se descargan URLs http/https públicas, sin seguir
/// redirecciones hacia redes privadas, con tamaño y tiempo acotados.
/// </summary>
public sealed class UrlDownloadOptions
{
    /// <summary>Bytes máximos de una descarga (por defecto 25 MB; nunca más que <see cref="MarkdownConverterOptions.MaxInputBytes"/>). Se comprueba mientras se lee.</summary>
    public long MaxBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Tiempo máximo de toda la descarga, incluidas las redirecciones (por defecto 30 s).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Redirecciones máximas (por defecto 5). Cada destino se valida igual que la URL original.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>
    /// Permite descargar desde loopback, redes privadas (RFC 1918), link-local (incluido 169.254.169.254, los
    /// metadatos de la nube), CGNAT, direcciones locales únicas IPv6, multicast y otras reservadas. Desactívelo solo si
    /// las URLs vienen de una fuente de confianza: con él, quien controle la URL puede leer servicios internos.
    /// </summary>
    public bool AllowPrivateNetworks { get; init; }

    /// <summary>
    /// Validación adicional de cada URL, incluidas las de las redirecciones (p. ej. una lista de dominios permitidos).
    /// Si devuelve false, la descarga se rechaza.
    /// </summary>
    public Func<Uri, bool>? UrlValidator { get; init; }

    /// <summary>
    /// Decide a qué direcciones IP se puede conectar, en lugar de la regla por defecto (<see cref="UrlDownloader.IsPublicAddress"/>,
    /// o cualquiera con <see cref="AllowPrivateNetworks"/>). Se evalúa con cada dirección resuelta, al conectar.
    /// </summary>
    public Func<IPAddress, bool>? AddressValidator { get; init; }
}

/// <summary>Descarga rechazada (esquema, dirección o URL no permitidos, demasiadas redirecciones, error HTTP o tiempo agotado).</summary>
public sealed class UrlDownloadException : AiSdkException
{
    public UrlDownloadException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Contenido descargado: bytes, tipo MIME informado por el servidor (o la URL <c>data:</c>) y URL final.</summary>
public sealed record UrlDownloadResult(byte[] Data, string? MediaType, Uri Url);

/// <summary>
/// Descargas con protección SSRF: solo http/https (y URLs <c>data:</c>), conexión directa (sin proxy) a la dirección IP
/// ya validada —así un DNS que cambia entre la validación y la conexión no sirve para llegar a una red interna—,
/// redirecciones manuales validadas en cada salto, tamaño máximo comprobado mientras se lee y tiempo máximo.
/// </summary>
public static class UrlDownloader
{
    /// <summary>Descarga <paramref name="url"/> con las protecciones de <paramref name="options"/>.</summary>
    /// <exception cref="UrlDownloadException">URL, esquema o dirección no permitidos, error HTTP, demasiadas redirecciones o tiempo agotado.</exception>
    /// <exception cref="DocumentTooLargeException">El contenido supera <see cref="UrlDownloadOptions.MaxBytes"/>.</exception>
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
                            throw new UrlDownloadException($"{Describe(current)} respondió {(int)response.StatusCode} sin Location.");
                        }

                        if (redirects >= options.MaxRedirects)
                        {
                            throw new UrlDownloadException($"La descarga de {Describe(url)} superó el máximo de {options.MaxRedirects} redirecciones.");
                        }

                        current = location.IsAbsoluteUri ? location : new Uri(current, location);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new UrlDownloadException($"{Describe(current)} respondió {(int)response.StatusCode} {response.ReasonPhrase}.");
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
            throw new UrlDownloadException($"La descarga de {Describe(url)} superó el tiempo máximo de {options.Timeout.TotalSeconds:0.#} s.", ex);
        }
    }

    /// <summary>
    /// True si la dirección es pública (unicast global). Rechaza loopback, "esta red" (0.0.0.0/8), RFC 1918, CGNAT
    /// (100.64.0.0/10), link-local (169.254.0.0/16 y fe80::/10), redes de documentación y pruebas, multicast,
    /// reservadas, locales únicas IPv6 (fc00::/7), y las IPv6 que encapsulan una IPv4 (mapeadas, NAT64, 6to4, Teredo)
    /// cuando la IPv4 no es pública.
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
            throw new UrlDownloadException($"Solo se descargan URLs http o https: {Describe(url)}.");
        }

        if (options.UrlValidator is { } validator && !validator(url))
        {
            throw new UrlDownloadException($"La URL {Describe(url)} no está permitida (UrlValidator).");
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

            throw new HttpRequestException($"No se pudo conectar a '{host}'.", lastError);
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

    /// <summary>URL <c>data:[tipo][;base64],datos</c>, comprobando el tamaño antes de decodificar.</summary>
    private static UrlDownloadResult DecodeDataUrl(Uri url, long maxBytes)
    {
        var raw = url.OriginalString;
        var comma = raw.IndexOf(',', StringComparison.Ordinal);
        if (comma < 5)
        {
            throw new UrlDownloadException("URL data: inválida (falta la coma).");
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
            throw new UrlDownloadException("URL data: con base64 inválido.", ex);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.MultipleChoices;

    private static string BlockedMessage(string host, IReadOnlyCollection<IPAddress> addresses) =>
        $"Descarga bloqueada: '{host}' corresponde a una dirección no pública ({string.Join(", ", addresses)}). " +
        "Use UrlDownloadOptions.AllowPrivateNetworks o AddressValidator solo si la URL es de confianza.";

    /// <summary>URL sin credenciales ni consulta, para mensajes de error.</summary>
    private static string Describe(Uri url) =>
        url.IsAbsoluteUri ? $"'{url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped)}'" : $"'{url}'";
}
