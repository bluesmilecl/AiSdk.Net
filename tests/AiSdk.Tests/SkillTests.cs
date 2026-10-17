using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AiSdk.Anthropic;
using AiSdk.Skills;

namespace AiSdk.Tests;

public class SkillTests
{
    private static readonly string[] ScriptArguments = ["a", "b", "c"];

    private const string ContractSkill =
        """
        ---
        name: revisar-contratos
        description: >-
          Revisa contratos de arriendo y detecta cláusulas abusivas.
          USE FOR: contratos de arriendo, revisar cláusulas. DO NOT USE FOR: redactar contratos nuevos.
        license: MIT
        compatibility: "Requiere acceso a internet: no"
        metadata:
          author: legal-team
          version: "1.2"
        allowed-tools: read_skill_file load_skill
        ---

        # Revisión de contratos

        ## Flujo

        1. Identifica a las partes y el plazo.
        2. Compara cada cláusula con [la guía](references/clausulas.md).
        3. Resume los riesgos en una tabla.

        ```text
        | Cláusula | Riesgo |
        ```
        """;

    private static Skill ContractSkillWithFiles(bool trusted = true) => Skill.Parse(
        ContractSkill,
        SkillFiles.FromText(new Dictionary<string, string>
        {
            ["references/clausulas.md"] = "La multa no puede superar un mes de arriendo.",
            ["scripts/contar.py"] = "import sys\nprint(len(sys.argv) - 1)\nopen('salida.txt','w').write('ok')",
        }),
        trusted: trusted);

    [Fact]
    public void ParsesTheAgentSkillsFormat()
    {
        var skill = ContractSkillWithFiles();

        Assert.Equal("revisar-contratos", skill.Name);
        Assert.StartsWith("Revisa contratos de arriendo y detecta cláusulas abusivas. USE FOR:", skill.Description);
        Assert.Equal("MIT", skill.License);
        Assert.Equal("Requiere acceso a internet: no", skill.Compatibility);
        Assert.Equal("1.2", skill.Metadata["version"]);
        Assert.Equal(["read_skill_file", "load_skill"], skill.AllowedTools);
        Assert.StartsWith("# Revisión de contratos", skill.Instructions);
        Assert.Equal(["references/clausulas.md", "scripts/contar.py"], skill.Files.List());
        Assert.True(skill.ModelInvocable);
    }

    [Fact]
    public void WritesAndReparsesTheSameSkill()
    {
        var skill = ContractSkillWithFiles();
        var again = Skill.Parse(skill.ToMarkdown());

        Assert.Equal(skill.Name, again.Name);
        Assert.Equal(skill.Description, again.Description);
        Assert.Equal(skill.Compatibility, again.Compatibility);
        Assert.Equal(skill.Metadata, again.Metadata);
        Assert.Equal(skill.AllowedTools, again.AllowedTools);
        Assert.Equal(skill.Instructions, again.Instructions);
    }

    [Theory]
    [InlineData("Mi-Skill", "minúsculas")]
    [InlineData("-skill", "guion")]
    [InlineData("mi--skill", "consecutivos")]
    public void ValidatesNamesAgainstTheSpec(string name, string expected)
    {
        var skill = new Skill { Name = name, Description = "Hace algo útil. Úsala cuando haga falta.", Instructions = "# x" };
        var result = skill.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatesDescriptionsReferencesAndFolderNames()
    {
        var bad = new Skill
        {
            Name = "ok",
            Description = "Usa <Vector> " + new string('x', 1100),
            Instructions = "Ver [esto](../fuera.md) y [aquello](references/a/b/c.md).",
            Compatibility = new string('y', 501),
        };

        var errors = bad.Validate("otra-carpeta").Errors;
        Assert.Contains(errors, e => e.Contains("1024"));
        Assert.Contains(errors, e => e.Contains("XML"));
        Assert.Contains(errors, e => e.Contains("sale de la carpeta"));
        Assert.Contains(errors, e => e.Contains("más de un nivel"));
        Assert.Contains(errors, e => e.Contains("compatibility"));
        Assert.Contains(errors, e => e.Contains("no coincide con la carpeta"));

        var warnings = ContractSkillWithFiles().Validate().Warnings;
        Assert.DoesNotContain(warnings, w => w.Contains("no existe")); // la referencia existe
        Assert.Contains(warnings, w => w.Contains("escuetas"));
    }

    [Fact]
    public async Task LoadsFromDirectoriesAndSavesSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "aisdk-skills-" + Guid.NewGuid().ToString("N"));
        try
        {
            await SkillLibrary.SaveAsync(ContractSkillWithFiles(), Path.Combine(root, "plugins", "legal", "skills"), TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "rota"));
            await File.WriteAllTextAsync(Path.Combine(root, "rota", "SKILL.md"), "---\nname: Rota\ndescription: x\n---\nhola", TestContext.Current.CancellationToken);

            var library = new SkillLibrary();
            var loaded = library.LoadDirectory(root, new SkillLoadOptions { Trusted = true });

            var skill = Assert.Single(loaded);
            Assert.True(skill.Trusted);
            Assert.Equal("La multa no puede superar un mes de arriendo.", Encoding.UTF8.GetString((await skill.Files.ReadAsync("references/clausulas.md", TestContext.Current.CancellationToken))!));
            Assert.Null(await skill.Files.ReadAsync("references/../../../../etc/passwd", TestContext.Current.CancellationToken).ContinueWith(t => t.IsFaulted ? null : t.Result));
            Assert.Single(library.LoadErrors); // "Rota" es inválida y se informa sin detener la carga
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LoadsFromZipPackagesLikeGitHubArchives()
    {
        var packed = await SkillLibrary.PackAsync(ContractSkillWithFiles(), TestContext.Current.CancellationToken);

        // Simula el ZIP de un repositorio: carpeta raíz "repo-main/" con skills en subcarpetas.
        using var repo = new MemoryStream();
        using (var archive = new ZipArchive(repo, ZipArchiveMode.Create, leaveOpen: true))
        using (var source = new ZipArchive(new MemoryStream(packed)))
        {
            foreach (var entry in source.Entries)
            {
                using var from = entry.Open();
                using var to = archive.CreateEntry("repo-main/skills/" + entry.FullName).Open();
                await from.CopyToAsync(to, TestContext.Current.CancellationToken);
            }

            using var other = new StreamWriter(archive.CreateEntry("repo-main/otros/ignorada/SKILL.md").Open());
            await other.WriteAsync("---\nname: ignorada\ndescription: No debería cargarse por el filtro de ruta.\n---\n# x");
        }

        repo.Position = 0;
        var library = new SkillLibrary();
        var loaded = library.LoadZip(repo, path: "skills");

        var skill = Assert.Single(loaded);
        Assert.Equal("revisar-contratos", skill.Name);
        Assert.False(skill.Trusted); // los ZIP de terceros no son de confianza por defecto
        Assert.Equal(["references/clausulas.md", "scripts/contar.py"], skill.Files.List());
    }

    [Fact]
    public void CatalogListsDescriptionsWithinBudget()
    {
        var library = new SkillLibrary([
            ContractSkillWithFiles(),
            new SkillBuilder("solo-usuario").WithDescription("Publica en producción. Úsala solo cuando el usuario lo pida.").WithInstructions("# Deploy\n\n1. Publica.").UserInvocableOnly().Build(),
        ]);

        var catalog = library.Catalog();
        Assert.Contains("<name>revisar-contratos</name>", catalog);
        Assert.DoesNotContain("solo-usuario", catalog); // no invocable por el modelo

        var tight = library.Catalog(maxChars: 60);
        Assert.Contains("<skill><name>revisar-contratos</name></skill>", tight);
        Assert.Contains("load_skill", library.SystemPrompt());
    }

    [Fact]
    public async Task AgentsActivateSkillsProgressively()
    {
        var library = new SkillLibrary([ContractSkillWithFiles()]);
        var activations = new List<string>();
        library.Activated += a => activations.Add(a.Skill);
        var model = new MockLanguageModel()
            .ReturnsToolCall("load_skill", new { name = "revisar-contratos" })
            .ReturnsToolCall("read_skill_file", new { skill = "revisar-contratos", path = "references/clausulas.md" })
            .ReturnsToolCall("read_skill_file", new { skill = "revisar-contratos", path = "../secreto" })
            .ReturnsText("La multa del contrato es abusiva.");
        var agent = new ToolLoopAgent(new AgentSettings { Model = model, Instructions = "Eres abogado.", Skills = library });

        var result = await agent.GenerateAsync("Revisa este contrato: multa de 3 meses.", TestContext.Current.CancellationToken);

        var request = model.Requests[0];
        Assert.Contains("Eres abogado.", request.Messages[0].Text);
        Assert.Contains("<name>revisar-contratos</name>", request.Messages[0].Text);
        Assert.DoesNotContain("Compara cada cláusula", request.Messages[0].Text); // instrucciones aún no cargadas
        Assert.Contains(request.Tools!, t => t.Name == "load_skill");
        Assert.DoesNotContain(request.Tools!, t => t.Name == "run_skill_script"); // sin ejecutor configurado

        Assert.Contains("Compara cada cláusula", result.Steps[0].ToolResults[0].Output.GetString());
        Assert.Contains("references/clausulas.md", result.Steps[0].ToolResults[0].Output.GetString());
        Assert.Equal("La multa no puede superar un mes de arriendo.", result.Steps[1].ToolResults[0].Output.GetString());
        Assert.True(result.Steps[2].ToolResults[0].IsError);
        Assert.Equal(["revisar-contratos"], activations);
        Assert.Equal(1, library.Usage["revisar-contratos"]);
    }

    [Fact]
    public void UsersInvokeSkillsWithSlashCommands()
    {
        var library = new SkillLibrary([
            new SkillBuilder("traducir").WithDescription("Traduce textos al inglés. Úsala cuando pidan traducir.").WithInstructions("# Traducir\n\n1. Traduce al inglés: $ARGUMENTS").Build(),
        ]);

        Assert.Equal("<skill name=\"traducir\">\n# Traducir\n\n1. Traduce al inglés: hola mundo\n</skill>", library.TryInvokeSlashCommand("/traducir hola mundo"));
        Assert.Null(library.TryInvokeSlashCommand("/no-existe x"));
        Assert.Null(library.TryInvokeSlashCommand("hola"));
    }

    [Fact]
    public async Task TrustedSkillsCanRunScripts()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/python3"), "python3 no instalado");
        var runner = new ProcessSkillScriptRunner();
        var trusted = new SkillLibrary([ContractSkillWithFiles(trusted: true)]);
        var model = new MockLanguageModel()
            .ReturnsToolCall("run_skill_script", new { skill = "revisar-contratos", script = "scripts/contar.py", arguments = ScriptArguments })
            .ReturnsText("listo");

        var result = await new ToolLoopAgent(new AgentSettings { Model = model, Skills = trusted, SkillOptions = new SkillToolOptions { ScriptRunner = runner } })
            .GenerateAsync("x", TestContext.Current.CancellationToken);

        var output = result.Steps[0].ToolResults[0].Output;
        Assert.Equal(0, output.GetProperty("exitCode").GetInt32());
        Assert.Equal("3", output.GetProperty("stdout").GetString()!.Trim());
        Assert.Equal(["salida.txt"], output.GetProperty("createdFiles").EnumerateArray().Select(e => e.GetString()));

        // Las skills no confiables no ejecutan scripts.
        var untrusted = new SkillLibrary([ContractSkillWithFiles(trusted: false)]);
        var denied = await new ToolLoopAgent(new AgentSettings
        {
            Model = new MockLanguageModel().ReturnsToolCall("run_skill_script", new { skill = "revisar-contratos", script = "scripts/contar.py" }).ReturnsText("x"),
            Skills = untrusted,
            SkillOptions = new SkillToolOptions { ScriptRunner = runner },
        }).GenerateAsync("x", TestContext.Current.CancellationToken);
        Assert.True(denied.Steps[0].ToolResults[0].IsError);
    }

    [Fact]
    public async Task ScriptsDoNotInheritSecretsAndTimeOut()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/python3"), "python3 no instalado");
        Environment.SetEnvironmentVariable("AISDK_TEST_SECRET", "s3cr3t");
        var skill = new SkillBuilder("entorno")
            .WithDescription("Prueba de entorno. Úsala cuando se pruebe el ejecutor.")
            .WithInstructions("# Entorno\n\n1. Ejecuta el script.")
            .AddFile("scripts/env.py", "import os\nprint(os.environ.get('AISDK_TEST_SECRET', 'sin-secreto'))")
            .AddFile("scripts/lento.py", "import time\ntime.sleep(5)")
            .Build();
        var runner = new ProcessSkillScriptRunner(new ProcessSkillScriptRunnerOptions { Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.Equal("sin-secreto", (await runner.RunAsync(skill, "scripts/env.py", [], TestContext.Current.CancellationToken)).Stdout.Trim());
        Assert.Equal(-1, (await runner.RunAsync(skill, "scripts/lento.py", [], TestContext.Current.CancellationToken)).ExitCode);
        await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(skill, "references/x.exe", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AgentsCreateValidatedSkills()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-authored-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new SkillLibrary();
            var model = new MockLanguageModel()
                .ReturnsToolCall("create_skill", new { name = "Mal Nombre", description = "x", instructions = "y" })
                .ReturnsToolCall("create_skill", new
                {
                    name = "resumir-reuniones",
                    description = "Resume actas de reuniones en decisiones y tareas. USE FOR: actas, minutas.",
                    instructions = "# Resumen de reuniones\n\n1. Lista las decisiones.\n2. Lista las tareas con responsable.",
                    files = new[] { new { path = "references/formato.md", content = "## Decisiones\n## Tareas" } },
                })
                .ReturnsText("Skill creada.");

            var result = await Ai.GenerateTextAsync(new GenerateTextOptions
            {
                Model = model,
                Prompt = "Guarda este procedimiento como skill",
                Tools = SkillAuthoringTools.Create(library, new SkillAuthoringOptions { Directory = dir }),
                StopWhen = StopCondition.StepCountIs(4),
            });

            Assert.False(result.Steps[0].ToolResults[0].Output.GetProperty("created").GetBoolean());
            Assert.True(result.Steps[1].ToolResults[0].Output.GetProperty("created").GetBoolean());
            var created = library.Get("resumir-reuniones")!;
            Assert.False(created.Trusted);
            Assert.True(File.Exists(Path.Combine(dir, "resumir-reuniones", "references", "formato.md")));

            // Se puede volver a cargar desde disco.
            var reloaded = new SkillLibrary().LoadDirectory(dir);
            Assert.Equal("resumir-reuniones", Assert.Single(reloaded).Name);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GeneratorRetriesUntilTheSkillIsValid()
    {
        var model = new MockLanguageModel()
            .ReturnsText("""{"name":"Bad Name","description":"Hace X. USE FOR: X.","instructions":"# X\n\n1. Paso"}""")
            .ReturnsText("""{"name":"hacer-x","description":"Hace X. USE FOR: X.","instructions":"# X\n\n1. Paso","references":[{"path":"references/x.md","content":"detalle"}]}""");

        var skill = await SkillGenerator.GenerateAsync(model, "Una skill para hacer X", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hacer-x", skill.Name);
        Assert.Equal(["references/x.md"], skill.Files.List());
        Assert.Contains("solo puede tener minúsculas", model.Requests[1].Messages[^1].Text);
    }

    [Fact]
    public async Task AnthropicContainerSkillsOptionsAndReuse()
    {
        var handler = new FakeHandler()
            .Json("""
                {"id":"msg_1","model":"claude-opus-5-5","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1},
                 "container":{"id":"container_abc","expires_at":"2099-01-01T00:00:00Z"},
                 "content":[
                   {"type":"server_tool_use","id":"t1","name":"bash_code_execution","input":{"command":"python make.py"}},
                   {"type":"bash_code_execution_tool_result","tool_use_id":"t1","content":{"type":"bash_code_execution_result","stdout":"ok","content":[{"type":"bash_code_execution_output","file_id":"file_123"}]}},
                   {"type":"text","text":"Presentación lista."}]}
                """)
            .Json("""{"id":"msg_2","model":"claude-opus-5-5","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1},"content":[{"type":"text","text":"Agregué una diapositiva."}]}""");
        var anthropic = new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });
        var options = AnthropicSkills.Options([AnthropicSkill.PowerPoint, AnthropicSkill.Custom("skill_01x", "3")], baseOptions: AnthropicOptions.Create(effort: "high"));

        var first = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = anthropic.Messages("claude-opus-5-5"),
            Prompt = "Crea una presentación",
            Tools = [AnthropicTools.CodeExecution()],
            ProviderOptions = options,
        });

        var (request, body) = handler.Requests[0];
        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
        Assert.Equal("pptx", (string?)body["container"]!["skills"]![0]!["skill_id"]);
        Assert.Equal("3", (string?)body["container"]!["skills"]![1]!["version"]);
        Assert.Contains("skills-2025-10-02", request.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal(["file_123"], AnthropicSkills.GetGeneratedFileIds(first));
        Assert.Equal("container_abc", AnthropicSkills.GetContainerId(first.ResponseMessages));

        // Segundo turno: el contenedor se reutiliza y el marcador interno no se envía a la API.
        await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = anthropic.Messages("claude-opus-5-5"),
            Messages = [ModelMessage.User("Crea una presentación"), .. first.ResponseMessages, ModelMessage.User("Agrega una diapositiva")],
            Tools = [AnthropicTools.CodeExecution()],
            ProviderOptions = options,
        });
        var second = handler.Requests[1].Body;
        Assert.Equal("container_abc", (string?)second["container"]!["id"]);
        Assert.DoesNotContain("aisdk_container", second.ToJsonString());
    }

    [Fact]
    public async Task AnthropicSkillsApiUploadsSkillFolders()
    {
        var handler = new FakeHandler()
            .Json("""{"id":"skill_01abc","display_title":"Contratos","source":"custom","latest_version":"1759178010641129","created_at":"2026-10-01T00:00:00Z"}""")
            .Json("""{"data":[{"skill_id":"skill_01abc","version":"1"},{"skill_id":"skill_01abc","version":"2"}]}""")
            .Json("{}").Json("{}").Json("{}");
        var anthropic = new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        var created = await anthropic.Skills.CreateAsync(ContractSkillWithFiles(), "Contratos", TestContext.Current.CancellationToken);
        await anthropic.Skills.DeleteAsync("skill_01abc", TestContext.Current.CancellationToken);

        Assert.Equal("skill_01abc", created.Id);
        var upload = handler.Requests[0];
        Assert.Equal("/v1/skills", upload.Request.RequestUri!.AbsolutePath);
        Assert.Contains("revisar-contratos/SKILL.md", upload.Raw);
        Assert.Contains("revisar-contratos/references/clausulas.md", upload.Raw);
        Assert.Contains("name=display_title", upload.Raw);
        // Elimina las versiones antes que la skill.
        Assert.Equal(
            ["/v1/skills/skill_01abc/versions", "/v1/skills/skill_01abc/versions/1", "/v1/skills/skill_01abc/versions/2", "/v1/skills/skill_01abc"],
            handler.Requests.Skip(1).Select(r => r.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task AnthropicFilesApiSendsTheBetaHeaderAndDownloads()
    {
        var handler = new FakeHandler().Bytes([1, 2, 3], "application/octet-stream");
        var anthropic = new AnthropicProvider(new AnthropicProviderOptions { ApiKey = "k", HttpClient = new HttpClient(handler) });

        var bytes = await anthropic.Files.DownloadAsync("file_123", TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], bytes);
        Assert.Equal("/v1/files/file_123/content", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("files-api-2025-04-14", handler.Requests[0].Request.Headers.GetValues("anthropic-beta").Single());
    }
}
