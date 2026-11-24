using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;

namespace AiSdk.Skills;

/// <summary>Opciones de las herramientas de skills.</summary>
public sealed class SkillToolOptions
{
    /// <summary>
    /// Ejecutor de los scripts de las skills (<c>scripts/</c>). Si es null no se expone
    /// <c>run_skill_script</c>: el modelo solo lee instrucciones y archivos.
    /// </summary>
    public ISkillScriptRunner? ScriptRunner { get; init; }

    /// <summary>Permite ejecutar scripts de skills no confiables (de terceros). Desactivado por defecto.</summary>
    public bool AllowUntrustedScripts { get; init; }

    /// <summary>
    /// Si es true (por defecto), <c>run_skill_script</c> requiere aprobación humana (<see cref="CallSettings.OnToolApproval"/>)
    /// antes de cada ejecución: las instrucciones de cualquier skill, o el resultado de otra herramienta, podrían
    /// inducir al modelo a ejecutar el script de una skill de confianza. Sin manejador de aprobación la llamada falla
    /// con un error explícito. Desactívelo solo si los scripts son inocuos con cualquier argumento.
    /// </summary>
    public bool RequireScriptApproval { get; init; } = true;

    /// <summary>Caracteres máximos devueltos al leer un archivo.</summary>
    public int MaxFileChars { get; init; } = 60_000;

    /// <summary>Envía un data part <c>data-skill</c> a la interfaz al activar una skill.</summary>
    public bool NotifyUI { get; init; } = true;
}

/// <summary>
/// Herramientas que dan al agente acceso a una <see cref="SkillLibrary"/> con divulgación progresiva:
/// <c>load_skill</c> (instrucciones), <c>read_skill_file</c> (referencias y recursos) y, si se configura
/// un <see cref="ISkillScriptRunner"/>, <c>run_skill_script</c>.
/// </summary>
public static class SkillTools
{
    public static IReadOnlyList<Tool> Create(SkillLibrary library, SkillToolOptions? options = null)
    {
        options ??= new SkillToolOptions();
        var o = options;

        Skill Find(string name) => library.Get(name) ?? throw new ArgumentException(
            $"No existe la skill '{name}'. Disponibles: {string.Join(", ", library.Skills.Where(s => s.ModelInvocable).Select(s => s.Name))}.");

        var tools = new List<Tool>
        {
            Tool.Create<LoadInput, string>(
                "load_skill",
                "Carga las instrucciones completas de una skill del catálogo <available_skills>. Úsala antes de realizar una tarea que coincida con su descripción.",
                (input, ctx, _) =>
                {
                    var skill = Find(input.Name);
                    if (!skill.ModelInvocable)
                    {
                        throw new InvalidOperationException($"La skill '{skill.Name}' solo puede invocarla el usuario.");
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
                        builder.Append("\n\nArchivos de la skill (léelos con read_skill_file solo si los necesitas):\n");
                        builder.AppendJoin('\n', files.Select(f => "- " + f));
                    }

                    if (skill.Compatibility is { } compatibility)
                    {
                        builder.Append("\n\nCompatibilidad: ").Append(compatibility);
                    }

                    return Task.FromResult(builder.ToString());
                }),
            Tool.Create<ReadInput, string>(
                "read_skill_file",
                "Lee un archivo de una skill (p. ej. references/guia.md o assets/plantilla.html).",
                async (input, _, ct) =>
                {
                    var skill = Find(input.Skill);
                    var path = SkillFiles.NormalizePath(input.Path);
                    var data = await skill.Files.ReadAsync(path, ct).ConfigureAwait(false)
                               ?? throw new ArgumentException($"La skill '{skill.Name}' no tiene el archivo '{path}'. Archivos: {string.Join(", ", skill.Files.List())}.");
                    if (IsBinary(data))
                    {
                        return $"[Archivo binario '{path}' de {data.Length} bytes; no se puede mostrar como texto.]";
                    }

                    var text = Encoding.UTF8.GetString(data);
                    return text.Length <= o.MaxFileChars ? text : text[..o.MaxFileChars] + $"\n\n[… recortado: {text.Length - o.MaxFileChars} caracteres más]";
                }),
        };

        if (o.ScriptRunner is { } runner)
        {
            var run = Tool.Create<RunInput, object>(
                "run_skill_script",
                "Ejecuta un script incluido en una skill (scripts/...) y devuelve su salida.",
                async (input, _, ct) =>
                {
                    var skill = Find(input.Skill);
                    if (!skill.Trusted && !o.AllowUntrustedScripts)
                    {
                        throw new UnauthorizedAccessException($"La skill '{skill.Name}' no es de confianza: sus scripts están deshabilitados.");
                    }

                    var result = await runner.RunAsync(skill, SkillFiles.NormalizePath(input.Script), input.Arguments ?? [], ct).ConfigureAwait(false);
                    return new { exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr, createdFiles = result.CreatedFiles.Keys, omittedFiles = result.OmittedFiles };
                });
            tools.Add(o.RequireScriptApproval ? run.RequireApproval() : run);
        }

        return tools;
    }

    private static bool IsBinary(byte[] data) => data.AsSpan(0, Math.Min(data.Length, 8000)).Contains((byte)0);

    private sealed record LoadInput([property: Description("Nombre de la skill.")] string Name);

    private sealed record ReadInput(
        [property: Description("Nombre de la skill.")] string Skill,
        [property: Description("Ruta relativa dentro de la skill.")] string Path);

    private sealed record RunInput(
        [property: Description("Nombre de la skill.")] string Skill,
        [property: Description("Ruta del script, p. ej. scripts/convertir.py.")] string Script,
        [property: Description("Argumentos de línea de comandos.")] IReadOnlyList<string>? Arguments = null);
}

/// <summary>Resultado de ejecutar un script de una skill.</summary>
public sealed record SkillScriptResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Archivos que el script creó o modificó (ruta relativa → contenido).</summary>
    public IReadOnlyDictionary<string, byte[]> CreatedFiles { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>Archivos creados que no se devolvieron por superar los límites de tamaño o cantidad, o por ser enlaces.</summary>
    public IReadOnlyList<string> OmittedFiles { get; init; } = [];
}

/// <summary>Ejecuta scripts de skills. Implemente esta interfaz para usar un contenedor o sandbox propio.</summary>
public interface ISkillScriptRunner
{
    Task<SkillScriptResult> RunAsync(Skill skill, string script, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Opciones de <see cref="ProcessSkillScriptRunner"/>.</summary>
public sealed class ProcessSkillScriptRunnerOptions
{
    /// <summary>Intérprete por extensión. Solo se ejecutan scripts con extensión registrada.</summary>
    public Dictionary<string, string> Interpreters { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "python3",
        [".sh"] = "bash",
        [".js"] = "node",
        [".mjs"] = "node",
        [".ps1"] = "pwsh",
    };

    /// <summary>Tiempo máximo de ejecución (60 s por defecto). Al superarlo se detiene el árbol de procesos.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Caracteres máximos de stdout/stderr (el resto se lee y se descarta, sin acumularlo en memoria).</summary>
    public int MaxOutputChars { get; init; } = 30_000;

    /// <summary>Variables de entorno adicionales (el resto del entorno no se hereda, salvo PATH y LANG).</summary>
    public Dictionary<string, string> Environment { get; init; } = [];

    /// <summary>Tamaño máximo de cada archivo creado que se devuelve.</summary>
    public long MaxCreatedFileBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Cantidad máxima de archivos creados que se devuelven.</summary>
    public int MaxCreatedFiles { get; init; } = 100;

    /// <summary>Tamaño total máximo de los archivos creados que se devuelven.</summary>
    public long MaxCreatedTotalBytes { get; init; } = 50 * 1024 * 1024;
}

/// <summary>
/// Ejecuta scripts como procesos locales en una copia temporal de la skill, sin heredar el entorno
/// (no expone claves de API), con tiempo límite y salida acotada. Al terminar, por error, tiempo límite o
/// cancelación del llamador, detiene siempre el árbol de procesos. No es un sandbox: para skills de
/// terceros use un contenedor (implemente <see cref="ISkillScriptRunner"/>).
/// </summary>
public sealed class ProcessSkillScriptRunner(ProcessSkillScriptRunnerOptions? options = null) : ISkillScriptRunner
{
    private readonly ProcessSkillScriptRunnerOptions _options = options ?? new ProcessSkillScriptRunnerOptions();

    public async Task<SkillScriptResult> RunAsync(Skill skill, string script, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!_options.Interpreters.TryGetValue(Path.GetExtension(script), out var interpreter))
        {
            throw new NotSupportedException($"No hay intérprete configurado para '{Path.GetExtension(script)}'.");
        }

        if (!skill.Files.List().Contains(script))
        {
            throw new ArgumentException($"La skill '{skill.Name}' no tiene el script '{script}'.");
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

            using var process = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar '{interpreter}'.");
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
                return new SkillScriptResult(-1, stdout.Text, $"{stderr.Text}\n[El script superó el tiempo límite de {_options.Timeout.TotalSeconds:0} s y se detuvo.]".TrimStart('\n'));
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

    /// <summary>Archivos creados o modificados, con límites de cantidad y tamaño; no sigue enlaces.</summary>
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

    /// <summary>Lee una salida sin acumular más de <c>maxChars</c>; el resto se descarta para no bloquear al proceso.</summary>
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
                    return _dropped == 0 ? _text.ToString() : _text + $"\n[… salida recortada: {_dropped} caracteres más]";
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
