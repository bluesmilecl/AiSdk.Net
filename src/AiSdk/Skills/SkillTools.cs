using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using AiSdk.Resources;
using System.Globalization;

namespace AiSdk.Skills;

/// <summary>Options for the skill tools.</summary>
public sealed class SkillToolOptions
{
    /// <summary>
    /// Gets or sets the runner for skill scripts (<c>scripts/</c>). If null,
    /// <c>run_skill_script</c> is not exposed: the model only reads instructions and files.
    /// </summary>
    public ISkillScriptRunner? ScriptRunner { get; init; }

    /// <summary>
    /// Gets or sets whether scripts from untrusted (third-party) skills can run. Disabled by default.
    /// </summary>
    public bool AllowUntrustedScripts { get; init; }

    /// <summary>
    /// Gets or sets whether <c>run_skill_script</c> requires human approval (<see cref="CallSettings.OnToolApproval"/>)
    /// before each execution (true by default): the instructions of any skill, or the result of another tool, could
    /// induce the model to run the script of a trusted skill. Without an approval handler the call fails
    /// with an explicit error. Disable it only if the scripts are harmless with any arguments.
    /// </summary>
    public bool RequireScriptApproval { get; init; } = true;

    /// <summary>Gets or sets the maximum number of characters returned when reading a file.</summary>
    public int MaxFileChars { get; init; } = 60_000;

    /// <summary>
    /// Gets or sets whether a <c>data-skill</c> data part is sent to the UI when a skill is activated.
    /// </summary>
    public bool NotifyUI { get; init; } = true;
}

/// <summary>
/// Tools that give the agent access to a <see cref="SkillLibrary"/> with progressive disclosure:
/// <c>load_skill</c> (instructions), <c>read_skill_file</c> (references and resources) and, if
/// an <see cref="ISkillScriptRunner"/> is configured, <c>run_skill_script</c>.
/// </summary>
public static class SkillTools
{
    public static IReadOnlyList<Tool> Create(SkillLibrary library, SkillToolOptions? options = null)
    {
        options ??= new SkillToolOptions();
        var o = options;

        Skill Find(string name) => library.Get(name) ?? throw new ArgumentException(
            string.Format(CultureInfo.CurrentCulture, Strings.Skill_NotFoundAvailable, name, string.Join(", ", library.Skills.Where(s => s.ModelInvocable).Select(s => s.Name))));

        var tools = new List<Tool>
        {
            Tool.Create<LoadInput, string>(
                "load_skill",
                Strings.Skill_LoadDescription,
                (input, ctx, _) =>
                {
                    var skill = Find(input.Name);
                    if (!skill.ModelInvocable)
                    {
                        throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_UserInvocableOnly, skill.Name));
                    }

                    library.RecordActivation(skill.Name);
                    if (o.NotifyUI)
                    {
                        ctx.WriteData("skill", new { name = skill.Name, description = skill.Description }, id: $"skill-{skill.Name}", transient: false);
                    }

                    var files = skill.Files.List();
                    var builder = new StringBuilder($"<skill name=\"{skill.Name}\">\n{skill.Instructions}\n</skill>");
                    if (files.Count > 0)
                    {
                        builder.Append("\n\n").Append(Strings.Skill_FilesHeader).Append('\n');
                        builder.AppendJoin('\n', files.Select(f => "- " + f));
                    }

                    if (skill.Compatibility is { } compatibility)
                    {
                        builder.Append("\n\n").Append(string.Format(CultureInfo.CurrentCulture, Strings.Skill_Compatibility, compatibility));
                    }

                    return Task.FromResult(builder.ToString());
                }),
            Tool.Create<ReadInput, string>(
                "read_skill_file",
                Strings.Skill_ReadFileDescription,
                async (input, _, ct) =>
                {
                    var skill = Find(input.Skill);
                    var path = SkillFiles.NormalizePath(input.Path);
                    var data = await skill.Files.ReadAsync(path, ct).ConfigureAwait(false)
                               ?? throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_FileNotFound, skill.Name, path, string.Join(", ", skill.Files.List())));
                    if (IsBinary(data))
                    {
                        return string.Format(CultureInfo.CurrentCulture, Strings.Skill_BinaryFile, path, data.Length);
                    }

                    var text = Encoding.UTF8.GetString(data);
                    return text.Length <= o.MaxFileChars ? text : text[..o.MaxFileChars] + "\n\n" + string.Format(CultureInfo.CurrentCulture, Strings.Skill_FileTruncated, text.Length - o.MaxFileChars);
                }),
        };

        if (o.ScriptRunner is { } runner)
        {
            var run = Tool.Create<RunInput, object>(
                "run_skill_script",
                Strings.Skill_RunScriptDescription,
                async (input, _, ct) =>
                {
                    var skill = Find(input.Skill);
                    if (!skill.Trusted && !o.AllowUntrustedScripts)
                    {
                        throw new UnauthorizedAccessException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_UntrustedScripts, skill.Name));
                    }

                    var result = await runner.RunAsync(skill, SkillFiles.NormalizePath(input.Script), input.Arguments ?? [], ct).ConfigureAwait(false);
                    return new { exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr, createdFiles = result.CreatedFiles.Keys, omittedFiles = result.OmittedFiles };
                });
            tools.Add(o.RequireScriptApproval ? run.RequireApproval() : run);
        }

        return tools;
    }

    private static bool IsBinary(byte[] data) => data.AsSpan(0, Math.Min(data.Length, 8000)).Contains((byte)0);

    private sealed record LoadInput([property: LocalizedDescription(typeof(Strings), nameof(Strings.Skill_Param_Name))] string Name);

    private sealed record ReadInput(
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Skill_Param_Name))] string Skill,
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Skill_Param_Path))] string Path);

    private sealed record RunInput(
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Skill_Param_Name))] string Skill,
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Skill_Param_Script))] string Script,
        [property: LocalizedDescription(typeof(Strings), nameof(Strings.Skill_Param_Arguments))] IReadOnlyList<string>? Arguments = null);
}

/// <summary>Result of running a skill script.</summary>
public sealed record SkillScriptResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Gets the files the script created or modified (relative path → content).</summary>
    public IReadOnlyDictionary<string, byte[]> CreatedFiles { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>
    /// Gets the created files that were not returned because they exceeded the size or count limits, or were links.
    /// </summary>
    public IReadOnlyList<string> OmittedFiles { get; init; } = [];
}

/// <summary>Runs skill scripts. Implement this interface to use your own container or sandbox.</summary>
public interface ISkillScriptRunner
{
    Task<SkillScriptResult> RunAsync(Skill skill, string script, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Options for <see cref="ProcessSkillScriptRunner"/>.</summary>
public sealed class ProcessSkillScriptRunnerOptions
{
    /// <summary>Gets or sets the interpreter per extension. Only scripts with a registered extension are run.</summary>
    public Dictionary<string, string> Interpreters { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "python3",
        [".sh"] = "bash",
        [".js"] = "node",
        [".mjs"] = "node",
        [".ps1"] = "pwsh",
    };

    /// <summary>
    /// Gets or sets the maximum execution time (60 s by default). When exceeded, the process tree is stopped.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets the maximum characters of stdout/stderr (the rest is read and discarded, without
    /// buffering it in memory).
    /// </summary>
    public int MaxOutputChars { get; init; } = 30_000;

    /// <summary>
    /// Gets or sets additional environment variables (the rest of the environment is not inherited,
    /// except PATH and LANG).
    /// </summary>
    public Dictionary<string, string> Environment { get; init; } = [];

    /// <summary>Gets or sets the maximum size of each returned created file.</summary>
    public long MaxCreatedFileBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Gets or sets the maximum number of created files that are returned.</summary>
    public int MaxCreatedFiles { get; init; } = 100;

    /// <summary>Gets or sets the maximum total size of the created files that are returned.</summary>
    public long MaxCreatedTotalBytes { get; init; } = 50 * 1024 * 1024;
}

/// <summary>
/// Runs scripts as local processes in a temporary copy of the skill, without inheriting the environment (it does not
/// expose API keys), with a time limit and bounded output. When it finishes, whether by error, time limit or caller
/// cancellation, it always stops the process tree. It is not a sandbox: for third-party skills use a container
/// (implement <see cref="ISkillScriptRunner"/>).
/// </summary>
public sealed class ProcessSkillScriptRunner(ProcessSkillScriptRunnerOptions? options = null) : ISkillScriptRunner
{
    private readonly ProcessSkillScriptRunnerOptions _options = options ?? new ProcessSkillScriptRunnerOptions();

    public async Task<SkillScriptResult> RunAsync(Skill skill, string script, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!_options.Interpreters.TryGetValue(Path.GetExtension(script), out var interpreter))
        {
            throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_NoInterpreter, Path.GetExtension(script)));
        }

        if (!skill.Files.List().Contains(script))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_ScriptNotFound, skill.Name, script));
        }

        var workDir = Path.Combine(Path.GetTempPath(), "aisdk-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var before = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(workDir, "SKILL.md"), skill.ToMarkdown(), cancellationToken).ConfigureAwait(false);
            foreach (var path in skill.Files.List())
            {
                if (await skill.Files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is not { } data)
                {
                    continue;
                }

                var target = Path.Combine(workDir, SkillFiles.NormalizePath(path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, data, cancellationToken).ConfigureAwait(false);
                before[path] = File.GetLastWriteTimeUtc(target);
            }

            var info = new ProcessStartInfo(interpreter)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(script);
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            // Entorno mínimo: nada de secretos del proceso anfitrión.
            info.Environment.Clear();
            info.Environment["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            info.Environment["LANG"] = System.Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8";
            info.Environment["HOME"] = workDir;
            info.Environment["TMPDIR"] = workDir;
            info.Environment["SKILL_DIR"] = workDir;
            foreach (var (key, value) in _options.Environment)
            {
                info.Environment[key] = value;
            }

            using var process = Process.Start(info) ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Skill_ProcessStartFailed, interpreter));
            var stdout = new BoundedOutput(_options.MaxOutputChars);
            var stderr = new BoundedOutput(_options.MaxOutputChars);
            using var stopReading = new CancellationTokenSource();
            var reading = Task.WhenAll(stdout.ReadAsync(process.StandardOutput, stopReading.Token), stderr.ReadAsync(process.StandardError, stopReading.Token));
            var timedOut = false;
            try
            {
                process.StandardInput.Close();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.Timeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                    // La salida puede seguir abierta si el script dejó procesos hijos: se espera dentro del mismo plazo.
                    await reading.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                }
            }
            finally
            {
                // Siempre, también si el llamador canceló: un script cancelado no sigue corriendo sin límite
                // mientras se borra su carpeta de trabajo.
                StopProcessTree(process);
                await stopReading.CancelAsync().ConfigureAwait(false);
            }

            if (timedOut)
            {
                return new SkillScriptResult(-1, stdout.Text, (stderr.Text + "\n" + string.Format(CultureInfo.CurrentCulture, Strings.Skill_ScriptTimeout, _options.Timeout.TotalSeconds)).TrimStart('\n'));
            }

            var (created, omitted) = await CollectCreatedFilesAsync(workDir, before, cancellationToken).ConfigureAwait(false);
            return new SkillScriptResult(process.ExitCode, stdout.Text, stderr.Text)
            {
                CreatedFiles = created,
                OmittedFiles = omitted,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Created or modified files, with count and size limits; does not follow links.</summary>
    private async Task<(Dictionary<string, byte[]> Created, List<string> Omitted)> CollectCreatedFilesAsync(
        string workDir,
        Dictionary<string, DateTime> before,
        CancellationToken cancellationToken)
    {
        var created = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var omitted = new List<string>();
        var total = 0L;
        // No se entra en carpetas enlazadas ni se leen archivos enlazados: podrían apuntar a cualquier parte del sistema.
        var entries = new FileSystemEnumerable<(string Path, bool IsLink, bool IsDirectory)>(
            workDir,
            (ref FileSystemEntry e) => (e.ToFullPath(), (e.Attributes & FileAttributes.ReparsePoint) != 0, e.IsDirectory),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        foreach (var (path, isLink, isDirectory) in entries)
        {
            var relative = Path.GetRelativePath(workDir, path).Replace('\\', '/');
            if (relative == "SKILL.md" || (isDirectory && !isLink))
            {
                continue;
            }

            if (isLink)
            {
                omitted.Add(relative);
                continue;
            }

            var file = new FileInfo(path);
            var changed = !before.TryGetValue(relative, out var stamp) || file.LastWriteTimeUtc != stamp;
            if (!changed)
            {
                continue;
            }

            if (created.Count >= _options.MaxCreatedFiles || file.Length > _options.MaxCreatedFileBytes || total + file.Length > _options.MaxCreatedTotalBytes)
            {
                omitted.Add(relative);
                continue;
            }

            // Longitud 0: no se abre (un FIFO creado por el script bloquearía la lectura indefinidamente).
            var data = file.Length == 0 ? [] : await ReadAtMostAsync(file.FullName, file.Length, cancellationToken).ConfigureAwait(false);
            total += data.Length;
            created[relative] = data;
        }

        return (created, omitted);
    }

    private static async Task<byte[]> ReadAtMostAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        var buffer = new byte[maxBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static void StopProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // El proceso ya terminó.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No se pudo detener (permisos): no hay más que hacer.
        }
    }

    /// <summary>
    /// Reads an output without buffering more than <c>maxChars</c>; the rest is discarded so the process
    /// does not block.
    /// </summary>
    private sealed class BoundedOutput(int maxChars)
    {
        private readonly StringBuilder _text = new();
        private long _dropped;

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _dropped == 0 ? _text.ToString() : _text + "\n" + string.Format(CultureInfo.CurrentCulture, Strings.Skill_OutputTruncated, _dropped);
                }
            }
        }

        public async Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    lock (_text)
                    {
                        var keep = Math.Clamp(maxChars - _text.Length, 0, read);
                        _text.Append(buffer, 0, keep);
                        _dropped += read - keep;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Proceso detenido o lectura cancelada: se conserva lo leído.
            }
        }
    }
}
