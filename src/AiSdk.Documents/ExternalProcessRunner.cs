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

/// <summary>Opciones de <see cref="ExternalProcessRunner"/>.</summary>
public sealed class ExternalProcessOptions
{
    /// <summary>
    /// Variables habituales que una herramienta necesita para funcionar (PATH, HOME, idioma, temporales y, en Windows,
    /// las del sistema), además de <c>TESSDATA_PREFIX</c> y <c>OMP_THREAD_LIMIT</c>. Punto de partida para
    /// <see cref="AllowedEnvironmentVariables"/>.
    /// </summary>
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
    /// Si no es null, el proceso recibe solo estas variables del entorno actual (además de
    /// <see cref="EnvironmentVariables"/>), así no hereda cadenas de conexión ni claves. Una entrada terminada en
    /// <c>*</c> es un prefijo (p. ej. <c>"LC_*"</c>). Ver <see cref="CommonEnvironmentVariables"/>. Null hereda todo
    /// el entorno (comportamiento por defecto de .NET).
    /// </summary>
    public IReadOnlyCollection<string>? AllowedEnvironmentVariables { get; init; }

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

/// <summary>Ejecutor de procesos locales con envoltorio y entorno configurables.</summary>
/// <example>
/// <code>
/// // Herramientas con prioridad baja y sin heredar secretos del entorno de la aplicación.
/// ExternalProcess.Runner = new ExternalProcessRunner(new ExternalProcessOptions
/// {
///     Wrapper = "nice",
///     WrapperArguments = ["-n", "10"],
///     AllowedEnvironmentVariables = ExternalProcessOptions.CommonEnvironmentVariables,
///     EnvironmentVariables = new Dictionary&lt;string, string?&gt; { ["OMP_THREAD_LIMIT"] = "1" },
/// });
/// </code>
/// </example>
public sealed class ExternalProcessRunner(ExternalProcessOptions? options = null) : IExternalProcessRunner
{
    private readonly ConcurrentDictionary<string, bool> _available = new();

    public ExternalProcessOptions Options { get; } = options ?? new ExternalProcessOptions();

    /// <summary>
    /// Indica si la herramienta (salvo con <see cref="ExternalProcessOptions.CheckExecutable"/> en false) y el envoltorio,
    /// si hay uno, existen. El resultado se guarda en caché.
    /// </summary>
    public bool Exists(string executable) =>
        (Options.Wrapper is not null && !Options.CheckExecutable || _available.GetOrAdd(executable, ExistsOnPath))
        && (Options.Wrapper is null || _available.GetOrAdd(Options.Wrapper, ExistsOnPath));

    public async Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(CreateStartInfo(executable, arguments, input is not null))
                            ?? throw new InvalidOperationException($"No se pudo iniciar '{executable}'.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        using var output = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
        {
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // La herramienta terminó sin leer toda la entrada: su código de salida y su error lo explican.
            }
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ExternalProcess.Result(process.ExitCode, output.ToArray(), await stderr.ConfigureAwait(false));
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
