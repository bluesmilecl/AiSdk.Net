namespace AiSdk.Tests;

public class OrchestrationTests
{
    private static ToolLoopAgent Agent(string name, MockLanguageModel model, string? description = null) =>
        new(new AgentSettings { Name = name, Description = description, Model = model, Instructions = $"Eres {name}." });

    private static string LastUserText(LanguageModelRequest request) =>
        request.Messages.Last(m => m.Role == ChatRole.User).Text;

    [Fact]
    public async Task SequentialPassesEachOutputToTheNextStep()
    {
        var writer = new MockLanguageModel().ReturnsText("borrador");
        var editor = new MockLanguageModel().ReturnsText("versión final");
        var events = new List<OrchestrationEvent>();

        var result = await Orchestrations.Sequential(
                Agent("writer", writer),
                OrchestrationNodes.Function("upper", s => s.ToUpperInvariant()),
                Agent("editor", editor))
            .RunAsync("escribe un post", new OrchestrationOptions { OnEvent = e => { events.Add(e); return ValueTask.CompletedTask; } });

        Assert.Equal("versión final", result.Text);
        Assert.Equal("BORRADOR", LastUserText(editor.Requests[0]));
        Assert.Equal(["writer", "editor"], result.Runs.Select(r => r.Agent));
        Assert.Equal(20, result.TotalUsage.InputTokens); // 10 por agente; la función no consume tokens
        Assert.Equal(6, events.Count);
        Assert.Equal("editor", result.FinalAgent);
    }

    [Fact]
    public async Task ParallelRunsBranchesAndAggregates()
    {
        var legal = new MockLanguageModel().ReturnsText("sin riesgos legales");
        var finance = new MockLanguageModel().ReturnsText("ROI de 20%");
        var judge = new MockLanguageModel().ReturnsText("Aprobar");

        var result = await Orchestrations.Parallel([Agent("legal", legal), Agent("finanzas", finance)], Agent("comite", judge))
            .RunAsync("¿Compramos la empresa X?");

        Assert.Equal("Aprobar", result.Text);
        var prompt = LastUserText(judge.Requests[0]);
        Assert.Contains("Original task:\n¿Compramos la empresa X?", prompt);
        Assert.Contains("## legal\n\nsin riesgos legales", prompt);
        Assert.Contains("## finanzas\n\nROI de 20%", prompt);
        Assert.Equal("¿Compramos la empresa X?", LastUserText(legal.Requests[0]));
    }

    [Fact]
    public async Task RouterSendsToTheChosenRoute()
    {
        var classifier = new MockLanguageModel().ReturnsText("""{"route":"facturacion","reason":"pregunta por un cobro"}""");
        var billing = new MockLanguageModel().ReturnsText("Te devolvimos el cobro.");
        var support = new MockLanguageModel();
        RoutedEvent? routed = null;

        var result = await Orchestrations.Router(classifier, Agent("soporte", support, "Problemas técnicos"), Agent("facturacion", billing, "Cobros y pagos"))
            .RunAsync("Me cobraron dos veces", new OrchestrationOptions { OnEvent = e => { routed ??= e as RoutedEvent; return ValueTask.CompletedTask; } });

        Assert.Equal("Te devolvimos el cobro.", result.Text);
        Assert.Empty(support.Requests);
        Assert.Equal("facturacion", routed!.Route);
        Assert.Contains("- facturacion: Cobros y pagos", classifier.Requests[0].Messages[0].Text);
        Assert.Equal(["router", "facturacion"], result.Runs.Select(r => r.Agent));
    }

    [Fact]
    public async Task SupervisorDelegatesToWorkersInParallel()
    {
        var supervisor = new MockLanguageModel()
            .Returns(_ => new LanguageModelResult
            {
                Content =
                [
                    new ToolCallPart("c1", "investigador", System.Text.Json.JsonSerializer.SerializeToElement(new { task = "Precios de la competencia" })),
                    new ToolCallPart("c2", "analista", System.Text.Json.JsonSerializer.SerializeToElement(new { task = "Margen actual" })),
                ],
                FinishReason = FinishReason.ToolCalls,
            })
            .ReturnsText("Recomendación: subir 5%.");
        var researcher = new MockLanguageModel().ReturnsText("Competencia: $10");
        var analyst = new MockLanguageModel().ReturnsText("Margen: 30%");

        var result = await Orchestrations.Supervisor(supervisor, Agent("investigador", researcher, "Investiga"), Agent("analista", analyst, "Analiza"))
            .RunAsync("¿Subimos precios?");

        Assert.Equal("Recomendación: subir 5%.", result.Text);
        Assert.Equal("Precios de la competencia", LastUserText(researcher.Requests[0]));
        Assert.Contains(result.Runs, r => r.Agent == "analista" && r.Output == "Margen: 30%");
        Assert.Contains(result.Runs, r => r.Agent == "supervisor");
        var toolResults = supervisor.Requests[1].Messages.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Content.OfType<ToolResultPart>()).ToList();
        Assert.Equal(2, toolResults.Count);
    }

    [Fact]
    public async Task HandoffsTransferTheConversation()
    {
        var triage = new MockLanguageModel().ReturnsToolCall("transfer_to_ventas", new { reason = "quiere comprar" });
        var sales = new MockLanguageModel().ReturnsText("¡Claro! ¿Qué modelo te interesa?");
        HandoffEvent? handoff = null;

        var result = await Orchestrations.Handoffs(Agent("triage", triage), Agent("ventas", sales, "Ventas y cotizaciones"))
            .RunAsync("Quiero comprar un notebook", new OrchestrationOptions { OnEvent = e => { handoff ??= e as HandoffEvent; return ValueTask.CompletedTask; } });

        Assert.Equal("¡Claro! ¿Qué modelo te interesa?", result.Text);
        Assert.Equal("ventas", result.FinalAgent);
        Assert.Equal(("triage", "ventas", "quiere comprar"), (handoff!.Node, handoff.To, handoff.Reason));
        Assert.Single(triage.Requests); // se detuvo tras transferir
        // Ventas ve toda la conversación, incluido el traspaso, y puede devolverla a triage.
        Assert.Contains(sales.Requests[0].Messages, m => m.Role == ChatRole.Tool);
        Assert.Contains(sales.Requests[0].Tools!, t => t.Name == "transfer_to_triage");
        Assert.DoesNotContain(sales.Requests[0].Tools!, t => t.Name == "transfer_to_ventas");
        Assert.Equal(ChatRole.Assistant, result.Messages![^1].Role);
    }

    [Fact]
    public async Task EvaluatorOptimizerIteratesUntilApproved()
    {
        var generator = new MockLanguageModel().ReturnsText("Hola").ReturnsText("Estimado cliente: hola.");
        var evaluator = new MockLanguageModel()
            .ReturnsText("""{"approved":false,"score":3,"feedback":"Usa un saludo formal."}""")
            .ReturnsText("""{"approved":true,"score":9,"feedback":""}""");
        var evaluations = new List<EvaluationEvent>();

        var result = await Orchestrations.EvaluatorOptimizer(Agent("redactor", generator), evaluator, "Tono formal")
            .RunAsync("Escribe un saludo", new OrchestrationOptions { OnEvent = e => { if (e is EvaluationEvent ev) evaluations.Add(ev); return ValueTask.CompletedTask; } });

        Assert.Equal("Estimado cliente: hola.", result.Text);
        Assert.Contains("Revise it according to this evaluation:\nUsa un saludo formal.", LastUserText(generator.Requests[1]));
        Assert.Equal([false, true], evaluations.Select(e => e.Approved));
    }

    [Fact]
    public async Task OrchestrationsNestAndWorkAsTools()
    {
        var a = new MockLanguageModel().ReturnsText("A");
        var b = new MockLanguageModel().ReturnsText("B");
        var team = Orchestrations.Parallel([Agent("a", a), Agent("b", b)]);
        var boss = new MockLanguageModel().ReturnsToolCall("equipo", new { task = "hagan algo" }).ReturnsText("listo");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = boss,
            Prompt = "coordina",
            Tools = [team.AsTool("equipo", "Equipo en paralelo")],
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.Equal("listo", result.Text);
        Assert.Equal("## a\n\nA\n\n## b\n\nB", result.Steps[0].ToolResults[0].Output.GetString());
    }
}
