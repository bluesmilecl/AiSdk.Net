using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AiSdk.Documents;

/// <summary>
/// Lanza las herramientas de línea de comandos (tesseract, pdftoppm, ffmpeg...). Se reemplaza para ejecutarlas
/// en un entorno aislado (contenedor, <c>bwrap</c>, <c>firejail</c>, un servicio remoto) o para registrarlas y
/// probarlas. El ejecutor global es <see cref="ExternalProcess.Runner"/>; los componentes que lanzan procesos
/// también aceptan uno propio.
/// </summary>
public interface IExternalProcessRunner
{
    /// <summary>Indica si la herramienta se puede ejecutar (ruta absoluta o nombre en el PATH).</summary>
    bool Exists(string executable);

    /// <summary>Ejecuta la herramienta y devuelve su salida estándar, su error estándar y el código de salida.</summary>
    Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opciones de <see cref="ExternalProcessRunner"/>. Por defecto cada herramienta recibe solo las variables de
/// <see cref="CommonEnvironmentVariables"/> (no las claves ni cadenas de conexión de la aplicación), se detiene a los
/// 10 minutos y no puede escribir más de 64 MB en su salida estándar.
/// </summary>
public sealed class ExternalProcessOptions
{
    /// <summary>
    /// Variables habituales que una herramienta necesita para funcionar (PATH, HOME, idioma, temporales y, en Windows,
    /// las del sistema), además de <c>TESSDATA_PREFIX</c> y <c>OMP_THREAD_LIMIT</c>. Valor por defecto de
    /// <see cref="AllowedEnvironmentVariables"/>.
    /// </summary>
    /// <summary>
    /// Si es true (por defecto) y nadie fijó <c>OMP_THREAD_LIMIT</c>, las herramientas corren con
    /// <c>OMP_THREAD_LIMIT=1</c>. Tesseract usa OpenMP: cada proceso abre un hilo por núcleo y espera activamente, así que
    /// varias páginas en paralelo (como hace el SDK) se estorban: 4 páginas tardaban más de 10 minutos en 4 núcleos en vez
    /// de 2 segundos. Con un hilo por proceso el paralelismo lo da el SDK (una página por proceso).
    /// </summary>
    public bool LimitOpenMpThreads { get; init; } = true;

    public static IReadOnlyList<string> CommonEnvironmentVariables { get; } =
    [
        "PATH", "HOME", "LANG", "LANGUAGE", "LC_*", "TMPDIR", "TEMP", "TMP", "TZ",
        "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "SYSTEMDRIVE", "USERPROFILE",
        "TESSDATA_PREFIX", "OMP_THREAD_LIMIT",
    ];

    /// <summary>
    /// Programa que envuelve cada ejecución (p. ej. <c>nice</c>, <c>firejail</c> o un script propio): se lanza
    /// <c>Wrapper [WrapperArguments...] herramienta [argumentos...]</c>. Null ejecuta la herramienta directamente.
    /// </summary>
    public string? Wrapper { get; init; }

    /// <summary>Argumentos del envoltorio, antes del nombre de la herramienta (p. ej. <c>["-n", "10"]</c> para <c>nice</c>).</summary>
    public IReadOnlyList<string> WrapperArguments { get; init; } = [];

    /// <summary>
    /// Variables del entorno actual que recibe el proceso (además de <see cref="EnvironmentVariables"/>), así no hereda
    /// cadenas de conexión ni claves. Una entrada terminada en <c>*</c> es un prefijo (p. ej. <c>"LC_*"</c>). Por
    /// defecto <see cref="CommonEnvironmentVariables"/>. Asigne null de forma explícita para heredar todo el entorno.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedEnvironmentVariables { get; init; } = CommonEnvironmentVariables;

    /// <summary>
    /// Tiempo máximo de cada ejecución (por defecto 10 minutos): al superarlo se detiene el proceso con todos sus
    /// hijos y se lanza <see cref="TimeoutException"/>. Null o <see cref="Timeout.InfiniteTimeSpan"/>: sin límite.
    /// </summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Bytes máximos de la salida estándar (por defecto 64 MB): al superarlos se detiene el proceso con todos sus hijos
    /// y se lanza <see cref="InvalidOperationException"/>. Del error estándar se conservan los primeros
    /// <see cref="ExternalProcessRunner.MaxErrorBytes"/> bytes (el resto se descarta sin detener el proceso).
    /// </summary>
    public long MaxOutputBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Variables que se fijan en el proceso (un valor null la quita), p. ej. <c>OMP_THREAD_LIMIT=1</c> para Tesseract.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>Directorio de trabajo del proceso (por defecto el actual).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Si es true (por defecto), <see cref="ExternalProcessRunner.Exists"/> también exige que la herramienta esté en
    /// este equipo. Use false cuando el envoltorio la ejecuta en otro lugar (un contenedor, un servidor): entonces
    /// solo se comprueba el envoltorio.
    /// </summary>
    public bool CheckExecutable { get; init; } = true;
}

/// <summary>
/// Ejecutor de procesos locales con envoltorio, entorno, tiempo y salida configurables (ver <see cref="ExternalProcessOptions"/>).
/// </summary>
/// <example>
/// <code>
/// // Herramientas con prioridad baja, sin secretos del entorno y con a lo más 2 minutos por ejecución.
/// ExternalProcess.Runner = new ExternalProcessRunner(new ExternalProcessOptions
/// {
///     Wrapper = "nice",
///     WrapperArguments = ["-n", "10"],
///     EnvironmentVariables = new Dictionary&lt;string, string?&gt; { ["OMP_THREAD_LIMIT"] = "1" },
///     Timeout = TimeSpan.FromMinutes(2),
/// });
/// </code>
/// </example>
public sealed class ExternalProcessRunner(ExternalProcessOptions? options = null) : IExternalProcessRunner
{
    /// <summary>Bytes del error estándar que se conservan en <see cref="ExternalProcess.Result.Error"/> (64 KB).</summary>
    public const int MaxErrorBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, bool> _available = new();

    public ExternalProcessOptions Options { get; } = options ?? new ExternalProcessOptions();

    /// <summary>
    /// Indica si la herramienta (salvo con <see cref="ExternalProcessOptions.CheckExecutable"/> en false) y el envoltorio,
    /// si hay uno, existen. El resultado se guarda en caché.
    /// </summary>
    public bool Exists(string executable) =>
        (Options.Wrapper is not null && !Options.CheckExecutable || _available.GetOrAdd(executable, ExistsOnPath))
        && (Options.Wrapper is null || _available.GetOrAdd(Options.Wrapper, ExistsOnPath));

    /// <summary>
    /// Ejecuta la herramienta. Si supera <see cref="ExternalProcessOptions.Timeout"/> o
    /// <see cref="ExternalProcessOptions.MaxOutputBytes"/>, o se cancela, detiene el proceso con todos sus hijos.
    /// </summary>
    /// <exception cref="TimeoutException">Superó <see cref="ExternalProcessOptions.Timeout"/>.</exception>
    /// <exception cref="InvalidOperationException">No se pudo iniciar o superó <see cref="ExternalProcessOptions.MaxOutputBytes"/>.</exception>
    public async Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        var timeout = Options.Timeout is { } limit && limit > TimeSpan.Zero && limit != System.Threading.Timeout.InfiniteTimeSpan ? limit : (TimeSpan?)null;
        using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked?.Token ?? cancellationToken;
        var name = Path.GetFileName(executable);

        using var process = Process.Start(CreateStartInfo(executable, arguments, input is not null))
                            ?? throw new InvalidOperationException($"No se pudo iniciar '{executable}'.");
        using var registration = token.Register(() => Kill(process));

        var overflow = false;
        using var output = new MemoryStream();
        var stdout = CopyLimitedAsync(process.StandardOutput.BaseStream, output, Options.MaxOutputBytes, () =>
        {
            overflow = true;
            Kill(process);
        }, token);
        var stderr = ReadErrorAsync(process.StandardError.BaseStream, token);
        try
        {
            if (input is not null)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // La herramienta terminó sin leer toda la entrada: su código de salida y su error lo explican.
                }
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException($"'{name}' superó el tiempo máximo de {timeout} y se detuvo.");
        }

        if (overflow)
        {
            throw new InvalidOperationException($"'{name}' superó el máximo de {Options.MaxOutputBytes} bytes de salida y se detuvo.");
        }

        return new ExternalProcess.Result(process.ExitCode, output.ToArray(), await stderr.ConfigureAwait(false));
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Ya terminó o no se puede detener.
        }
    }

    /// <summary>Copia hasta <paramref name="maxBytes"/>; si se superan, llama a <paramref name="onOverflow"/> y deja de leer.</summary>
    private static async Task CopyLimitedAsync(Stream source, Stream destination, long maxBytes, Action onOverflow, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > maxBytes)
            {
                onOverflow();
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Error estándar: conserva los primeros <see cref="MaxErrorBytes"/> bytes y descarta el resto (sin bloquear al proceso).</summary>
    private static async Task<string> ReadErrorAsync(Stream source, CancellationToken cancellationToken)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[16384];
        var truncated = false;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var room = (int)Math.Min(read, MaxErrorBytes - kept.Length);
            kept.Write(buffer, 0, room);
            truncated |= room < read;
        }

        var text = System.Text.Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
        return truncated ? text + "…" : text;
    }

    /// <summary>Arma el <see cref="ProcessStartInfo"/> con el envoltorio y el entorno configurados (sin iniciarlo).</summary>
    public ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments, bool redirectInput = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(Options.Wrapper ?? executable)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Options.WorkingDirectory is { } workingDirectory)
        {
            info.WorkingDirectory = workingDirectory;
        }

        if (Options.Wrapper is not null)
        {
            foreach (var argument in Options.WrapperArguments)
            {
                info.ArgumentList.Add(argument);
            }

            info.ArgumentList.Add(executable);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (Options.AllowedEnvironmentVariables is { } allowed)
        {
            info.Environment.Clear();
            foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is string name && IsAllowed(name, allowed))
                {
                    info.Environment[name] = variable.Value as string;
                }
            }
        }

        foreach (var (name, value) in Options.EnvironmentVariables)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        if (Options.LimitOpenMpThreads && !info.Environment.ContainsKey("OMP_THREAD_LIMIT"))
        {
            info.Environment["OMP_THREAD_LIMIT"] = "1";
        }

        return info;
    }

    private static bool IsAllowed(string name, IReadOnlyCollection<string> allowed)
    {
        // Las variables de entorno de Windows no distinguen mayúsculas.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var entry in allowed)
        {
            if (entry.EndsWith('*') ? name.StartsWith(entry[..^1], comparison) : name.Equals(entry, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExistsOnPath(string name)
    {
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name);
        }

        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", string.Empty } : [string.Empty];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir, name + ext))));
    }
}

/// <summary>Extensiones de <see cref="IExternalProcessRunner"/>.</summary>
public static class ExternalProcessRunnerExtensions
{
    /// <summary>Ejecuta y lanza una excepción con el error estándar si el código de salida no es 0.</summary>
    public static async Task<byte[]> RunCheckedAsync(this IExternalProcessRunner runner, string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var result = await runner.RunAsync(executable, arguments, input, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException($"'{Path.GetFileName(executable)}' terminó con código {result.ExitCode}: {result.Error.Trim()}");
    }
}
