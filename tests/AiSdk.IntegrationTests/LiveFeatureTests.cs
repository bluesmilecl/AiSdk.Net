using AiSdk.Artifacts;
using AiSdk.Documents;
using AiSdk.Documents.Generation;
using AiSdk.Skills;

namespace AiSdk.IntegrationTests;

/// <summary>
/// Funciones de alto nivel (skills, orquestación, artifacts, documentos, compresión) contra modelos reales.
/// Cada proveedor se omite si su API key no está definida.
/// </summary>
public class LiveFeatureTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task AgentLoadsSkillAndReadsItsReference(string provider)
    {
        var library = new SkillLibrary();
        library.Add(new SkillBuilder("cotizar-envios")
            .WithDescription("Calcula el costo de envíos nacionales. USE FOR: cotizar envíos, costo de despacho, tarifas por peso.")
            .WithInstructions("# Cotizar envíos\n\n1. Lee las tarifas en [references/tarifas.md](references/tarifas.md).\n2. Total = Base + Por kg × peso.\n3. Responde con el total en pesos.")
            .AddFile("references/tarifas.md", "| Región | Base | Por kg |\n|---|---|---|\n| RM | $3.000 | $500 |\n| Sur | $5.000 | $800 |")
            .Build());
        var activated = new List<string>();
        library.Activated += a => activated.Add(a.Skill);
        var agent = new ToolLoopAgent(new AgentSettings { Model = LiveProviderTests.Model(provider), Skills = library });

        var result = await agent.GenerateAsync("¿Cuánto cuesta enviar 4 kg al Sur? Usa la skill de envíos.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("cotizar-envios", activated);
        Assert.Contains(result.Steps.SelectMany(s => s.ToolCalls), c => c.ToolName == "read_skill_file");
        Assert.Matches(@"8[.,]?200", result.Text);   // 5.000 + 800 × 4
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task ModelGeneratesAValidSkill(string provider)
    {
        var skill = await SkillGenerator.GenerateAsync(
            LiveProviderTests.Model(provider),
            "Revisar contratos de arriendo chilenos y señalar cláusulas abusivas.",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(SkillValidator.Validate(skill).IsValid, string.Join("; ", SkillValidator.Validate(skill).Errors));
        Assert.False(string.IsNullOrWhiteSpace(skill.Instructions));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task RouterChoosesTheRightAgent(string provider)
    {
        var model = LiveProviderTests.Model(provider);
        ToolLoopAgent Worker(string name, string description) =>
            new(new AgentSettings { Name = name, Description = description, Model = model, Instructions = $"Eres el agente de {description}. Responde en una frase." });
        RoutedEvent? routed = null;

        var result = await Orchestrations.Router(model, Worker("soporte", "problemas técnicos con la app"), Worker("facturacion", "cobros, boletas y pagos"))
            .RunAsync("Me cobraron dos veces la suscripción de este mes", new OrchestrationOptions
            {
                OnEvent = e => { routed ??= e as RoutedEvent; return ValueTask.CompletedTask; },
                CancellationToken = TestContext.Current.CancellationToken,
            });

        Assert.Equal("facturacion", routed?.Route);
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task AgentCreatesArtifactAndSpreadsheet(string provider)
    {
        var store = new InMemoryArtifactStore();
        var tools = ArtifactTools.Create(store).Concat(DocumentTools.Create(new DocumentToolOptions { Store = store })).ToList();

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = LiveProviderTests.Model(provider),
            Prompt = "Crea un artifact HTML con una calculadora de propinas y además una planilla Excel llamada 'ventas' con columnas Producto y Monto y dos filas de ejemplo.",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(6),
            CancellationToken = TestContext.Current.CancellationToken,
        });

        var artifacts = await store.ListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(artifacts, a => a.Type == ArtifactTypes.Html && a.Content!.Contains('<', StringComparison.Ordinal));
        var xlsx = Assert.Single(artifacts, a => a.FileName?.EndsWith(".xlsx", StringComparison.Ordinal) == true);
        var markdown = await new MarkdownConverter().ConvertAsync(xlsx.Data!, MediaTypes.Xlsx, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Producto", markdown.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Steps.SelectMany(s => s.ToolResults), r => r.IsError);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    public async Task SummarizesLongTextsWithMapReduce(string provider)
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => i == 17
            ? "Acuerdo clave: el contrato con Proveedor Andes se renueva el 1 de marzo por 24 meses."
            : $"Punto {i}: se revisaron indicadores operativos sin cambios relevantes respecto del mes anterior, y el equipo comentó detalles menores."));

        var summary = await ContextCompressor.SummarizeAsync(LiveProviderTests.Model(provider), text, targetTokens: 150, focus: "acuerdos y fechas", chunkTokens: 300, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(TokenEstimator.Estimate(summary) < TokenEstimator.Estimate(text) / 2);
        Assert.Contains("Andes", summary, StringComparison.OrdinalIgnoreCase);
    }
}
