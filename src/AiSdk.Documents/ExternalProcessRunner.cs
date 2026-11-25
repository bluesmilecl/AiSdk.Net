using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AiSdk.Documents.Resources;

namespace AiSdk.Documents;

/// <summary>
/// Launches the command-line tools (tesseract, pdftoppm, ffmpeg...). Replace it to run them in an isolated
/// environment (a container, <c>bwrap</c>, <c>firejail</c>, a remote service) or to log and test them. The global
/// runner is <see cref="ExternalProcess.Runner"/>; components that launch processes also accept their own.
/// </summary>
public interface IExternalProcessRunner
{
    /// <summary>Indicates whether the tool can be run (absolute path or name on the PATH).</summary>
    bool Exists(string executable);

    /// <summary>Runs the tool and returns its standard output, its standard error and the exit code.</summary>
    Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for <see cref="ExternalProcessRunner"/>. By default each tool receives only the variables in
/// <see cref="CommonEnvironmentVariables"/> (not the application's keys or connection strings), is stopped after
/// 10 minutes and cannot write more than 64 MB to its standard output.
/// </summary>
public sealed class ExternalProcessOptions
{
    /// <summary>
    /// If true (the default) and nobody has set <c>OMP_THREAD_LIMIT</c>, the tools run with
    /// <c>OMP_THREAD_LIMIT=1</c>. Tesseract uses OpenMP: each process opens one thread per core and busy-waits, so several
    /// pages in parallel (as the SDK does) get in each other's way: 4 pages took more than 10 minutes on 4 cores instead
    /// of 2 seconds. With one thread per process, the parallelism comes from the SDK (one page per process).
    /// </summary>
    public bool LimitOpenMpThreads { get; init; } = true;

    /// <summary>
    /// Common variables a tool needs in order to work (PATH, HOME, language, temporary directories and, on Windows, the
    /// system ones), plus <c>TESSDATA_PREFIX</c> and <c>OMP_THREAD_LIMIT</c>. Default value of
    /// <see cref="AllowedEnvironmentVariables"/>.
    /// </summary>
    public static IReadOnlyList<string> CommonEnvironmentVariables { get; } =
    [
        "PATH", "HOME", "LANG", "LANGUAGE", "LC_*", "TMPDIR", "TEMP", "TMP", "TZ",
        "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "SYSTEMDRIVE", "USERPROFILE",
        "TESSDATA_PREFIX", "OMP_THREAD_LIMIT",
    ];

    /// <summary>
    /// Program that wraps each execution (e.g. <c>nice</c>, <c>firejail</c> or a custom script): it is launched as
    /// <c>Wrapper [WrapperArguments...] tool [arguments...]</c>. Null runs the tool directly.
    /// </summary>
    public string? Wrapper { get; init; }

    /// <summary>Wrapper arguments, before the tool name (e.g. <c>["-n", "10"]</c> for <c>nice</c>).</summary>
    public IReadOnlyList<string> WrapperArguments { get; init; } = [];

    /// <summary>
    /// Variables from the current environment that the process receives (in addition to <see cref="EnvironmentVariables"/>),
    /// so it does not inherit connection strings or keys. An entry ending in <c>*</c> is a prefix (e.g. <c>"LC_*"</c>).
    /// Defaults to <see cref="CommonEnvironmentVariables"/>. Set it explicitly to null to inherit the whole environment.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedEnvironmentVariables { get; init; } = CommonEnvironmentVariables;

    /// <summary>
    /// Maximum time for each execution (10 minutes by default): when exceeded, the process is stopped along with all its
    /// children and <see cref="TimeoutException"/> is thrown. Null or <see cref="Timeout.InfiniteTimeSpan"/>: no limit.
    /// </summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum bytes of standard output (64 MB by default): when exceeded, the process is stopped along with all its
    /// children and <see cref="InvalidOperationException"/> is thrown. From standard error, the first
    /// <see cref="ExternalProcessRunner.MaxErrorBytes"/> bytes are kept (the rest is discarded without stopping the process).
    /// </summary>
    public long MaxOutputBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Variables set in the process (a null value removes it), e.g. <c>OMP_THREAD_LIMIT=1</c> for Tesseract.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>Working directory of the process (the current one by default).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// If true (the default), <see cref="ExternalProcessRunner.Exists"/> also requires the tool to be present on this
    /// machine. Use false when the wrapper runs it somewhere else (a container, a server): then only the wrapper is
    /// checked.
    /// </summary>
    public bool CheckExecutable { get; init; } = true;
}

/// <summary>
/// Local process runner with configurable wrapper, environment, time and output (see <see cref="ExternalProcessOptions"/>).
/// </summary>
/// <example>
/// <code>
/// // Tools at low priority, without environment secrets and with at most 2 minutes per execution.
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
    /// <summary>Bytes of standard error kept in <see cref="ExternalProcess.Result.Error"/> (64 KB).</summary>
    public const int MaxErrorBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, bool> _available = new();

    public ExternalProcessOptions Options { get; } = options ?? new ExternalProcessOptions();

    /// <summary>
    /// Indicates whether the tool (unless <see cref="ExternalProcessOptions.CheckExecutable"/> is false) and the wrapper,
    /// if any, exist. The result is cached.
    /// </summary>
    public bool Exists(string executable) =>
        (Options.Wrapper is not null && !Options.CheckExecutable || _available.GetOrAdd(executable, ExistsOnPath))
        && (Options.Wrapper is null || _available.GetOrAdd(Options.Wrapper, ExistsOnPath));

    /// <summary>
    /// Runs the tool. If it exceeds <see cref="ExternalProcessOptions.Timeout"/> or
    /// <see cref="ExternalProcessOptions.MaxOutputBytes"/>, or is cancelled, stops the process along with all its children.
    /// </summary>
    /// <exception cref="TimeoutException">Exceeded <see cref="ExternalProcessOptions.Timeout"/>.</exception>
    /// <exception cref="InvalidOperationException">Could not be started or exceeded <see cref="ExternalProcessOptions.MaxOutputBytes"/>.</exception>
    public async Task<ExternalProcess.Result> RunAsync(string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        var timeout = Options.Timeout is { } limit && limit > TimeSpan.Zero && limit != System.Threading.Timeout.InfiniteTimeSpan ? limit : (TimeSpan?)null;
        using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutSource is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked?.Token ?? cancellationToken;
        var name = Path.GetFileName(executable);

        using var process = Process.Start(CreateStartInfo(executable, arguments, input is not null))
                            ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Process_CouldNotStart, executable));
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
            throw new TimeoutException(string.Format(CultureInfo.CurrentCulture, Strings.Process_Timeout, name, timeout));
        }

        if (overflow)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Process_OutputTooLarge, name, Options.MaxOutputBytes));
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

    /// <summary>Copies up to <paramref name="maxBytes"/>; if exceeded, calls <paramref name="onOverflow"/> and stops reading.</summary>
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

    /// <summary>Standard error: keeps the first <see cref="MaxErrorBytes"/> bytes and discards the rest (without blocking the process).</summary>
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

    /// <summary>Builds the <see cref="ProcessStartInfo"/> with the configured wrapper and environment (without starting it).</summary>
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

/// <summary>Extensions for <see cref="IExternalProcessRunner"/>.</summary>
public static class ExternalProcessRunnerExtensions
{
    /// <summary>Runs the tool and throws an exception with the standard error if the exit code is not 0.</summary>
    public static async Task<byte[]> RunCheckedAsync(this IExternalProcessRunner runner, string executable, IReadOnlyList<string> arguments, byte[]? input = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var result = await runner.RunAsync(executable, arguments, input, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.Output
            : throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Process_ExitCode, Path.GetFileName(executable), result.ExitCode, result.Error.Trim()));
    }
}
