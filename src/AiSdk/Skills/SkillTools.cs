using System.ComponentModel;
using System.Diagnostics;
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
            tools.Add(Tool.Create<RunInput, object>(
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
                    return new { exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr, createdFiles = result.CreatedFiles.Keys };
                }));
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

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Caracteres máximos de stdout/stderr.</summary>
    public int MaxOutputChars { get; init; } = 30_000;

    /// <summary>Variables de entorno adicionales (el resto del entorno no se hereda, salvo PATH y LANG).</summary>
    public Dictionary<string, string> Environment { get; init; } = [];

    /// <summary>Tamaño máximo de los archivos creados que se devuelven.</summary>
    public long MaxCreatedFileBytes { get; init; } = 10 * 1024 * 1024;
}

/// <summary>
/// Ejecuta scripts como procesos locales en una copia temporal de la skill, sin heredar el entorno
/// (no expone claves de API), con tiempo límite y salida acotada. No es un sandbox: para skills de
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
                var target = Path.Combine(workDir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, (await skill.Files.ReadAsync(path, cancellationToken).ConfigureAwait(false))!, cancellationToken).ConfigureAwait(false);
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
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new SkillScriptResult(-1, string.Empty, $"El script superó el tiempo límite de {_options.Timeout.TotalSeconds:0} s.");
            }

            var created = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(workDir, file).Replace('\\', '/');
                var changed = !before.TryGetValue(relative, out var stamp) || File.GetLastWriteTimeUtc(file) != stamp;
                if (relative != "SKILL.md" && changed && new FileInfo(file).Length <= _options.MaxCreatedFileBytes)
                {
                    created[relative] = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            return new SkillScriptResult(process.ExitCode, Truncate(await stdout.ConfigureAwait(false)), Truncate(await stderr.ConfigureAwait(false)))
            {
                CreatedFiles = created,
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

    private string Truncate(string text) =>
        text.Length <= _options.MaxOutputChars ? text : text[.._options.MaxOutputChars] + "\n[… salida recortada]";
}
