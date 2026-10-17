using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiSdk.AspNetCore;
using AiSdk.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.Tests;

public class WorkflowTests
{
    private sealed record Order(string Id, decimal Amount);

    private static readonly StepOptions FastRetry = new() { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task StepsAreMemoizedAcrossDurableSleep()
    {
        var charges = 0;
        var engine = new WorkflowEngine().Register(Workflow.Create<Order, string>("onboarding", async (ctx, order) =>
        {
            var receipt = await ctx.StepAsync("charge", _ => Task.FromResult($"recibo-{order.Id}-{++charges}"));
            await ctx.SleepAsync("wait-1-day", TimeSpan.FromDays(1));
            var email = await ctx.StepAsync("follow-up", _ => Task.FromResult($"correo para {receipt}"));
            return email;
        }));

        var run = await engine.StartAsync("onboarding", new Order("A1", 10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowStatus.Sleeping, run.Status);
        Assert.True(run.ResumeAt > DateTimeOffset.UtcNow.AddHours(23));

        await engine.TickAsync(DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken); // aún no vence
        await engine.WhenIdleAsync();
        Assert.Equal(WorkflowStatus.Sleeping, (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.Status);

        // Simula que pasó el día: se reanuda y el cobro NO se repite.
        var stored = (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        stored.Steps["sleep:wait-1-day"] = stored.Steps["sleep:wait-1-day"] with { Output = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddSeconds(-1)) };
        stored.ResumeAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await engine.Store.SaveRunAsync(stored, TestContext.Current.CancellationToken);
        await engine.TickAsync(cancellationToken: TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();

        var done = (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(WorkflowStatus.Completed, done.Status);
        Assert.Equal("correo para recibo-A1-1", done.GetOutput<string>());
        Assert.Equal(1, charges);
        Assert.Equal(2, done.Executions);
    }

    [Fact]
    public async Task StepsRetryAndFailedRunsResumeWhereTheyStopped()
    {
        var attempts = 0;
        var firstStep = 0;
        var broken = true;
        var engine = new WorkflowEngine().Register(Workflow.Create<int, int>("calc", async (ctx, n) =>
        {
            var a = await ctx.StepAsync("first", _ => Task.FromResult(++firstStep * n));
            var b = await ctx.StepAsync("flaky", _ => ++attempts < 3 ? throw new HttpRequestException("503") : Task.FromResult(a + 1), FastRetry);
            return await ctx.StepAsync("final", _ => broken ? throw new NonRetryableException("dato inválido") : Task.FromResult(b * 10), FastRetry);
        }));

        var run = await engine.StartAsync("calc", 5, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowStatus.Failed, run.Status);
        Assert.Equal("dato inválido", run.Error);
        Assert.Equal(3, run.Steps["flaky"].Attempts);
        Assert.Contains(run.Logs, l => l.Contains("falló (intento 1/3)"));

        broken = false;
        var retried = await engine.RetryAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowStatus.Completed, retried.Status);
        Assert.Equal(60, retried.GetOutput<int>());
        Assert.Equal(1, firstStep); // no se repitió
    }

    private static WorkflowEngine ApprovalEngine(IWorkflowStore? store = null) =>
        new WorkflowEngine(store).Register(Workflow.Create<Order, string>("approval", async (ctx, order) =>
        {
            if (order.Amount <= 100)
            {
                return "aprobado automáticamente";
            }

            await ctx.StepAsync("notify-manager", _ => Task.FromResult(true));
            var decision = await ctx.WaitForEventAsync<JsonElement?>("order.approval", key: order.Id, timeout: TimeSpan.FromHours(48));
            return decision is { } d
                ? (d.GetProperty("approved").GetBoolean() ? "aprobado por gerente" : "rechazado")
                : "vencido";
        }));

    [Fact]
    public async Task HumanApprovalWithEvents()
    {
        var engine = ApprovalEngine();
        var run = await engine.StartAsync("approval", new Order("P-9", 5000), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowStatus.WaitingForEvent, run.Status);
        Assert.Equal("order.approval", run.Waiting!.EventName);

        Assert.Empty(await engine.SendEventAsync("order.approval", new { approved = true }, key: "OTRO", cancellationToken: TestContext.Current.CancellationToken));
        var resumed = await engine.SendEventAsync("order.approval", new { approved = true }, key: "P-9", cancellationToken: TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();

        Assert.Equal([run.Id], resumed);
        var done = (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal("aprobado por gerente", done.GetOutput<string>());
        Assert.True(done.Steps.ContainsKey("notify-manager"));
    }

    [Fact]
    public async Task EventWaitsTimeOut()
    {
        var engine = ApprovalEngine();
        var run = await engine.StartAsync("approval", new Order("P-1", 500), cancellationToken: TestContext.Current.CancellationToken);

        await engine.TickAsync(DateTimeOffset.UtcNow.AddHours(49), TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();

        // El plazo se evalúa contra el reloj real: forzamos el vencimiento en la espera guardada.
        var stored = (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!;
        stored.Waiting = stored.Waiting! with { TimeoutAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        stored.ResumeAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await engine.Store.SaveRunAsync(stored, TestContext.Current.CancellationToken);
        await engine.TickAsync(cancellationToken: TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();

        Assert.Equal("vencido", (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.GetOutput<string>());
    }

    [Fact]
    public async Task RunsSurviveARestartWithAPersistentStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-wf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = await ApprovalEngine(new FileSystemWorkflowStore(dir)).StartAsync("approval", new Order("P-7", 900), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowStatus.WaitingForEvent, run.Status);

            // "Reinicio": un motor nuevo con el mismo almacén retoma la ejecución.
            var engine = ApprovalEngine(new FileSystemWorkflowStore(dir));
            await engine.SendEventAsync("order.approval", new { approved = false }, key: "P-7", cancellationToken: TestContext.Current.CancellationToken);
            await engine.WhenIdleAsync();
            Assert.Equal("rechazado", (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.GetOutput<string>());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ScheduledAutomationsFireOnCronOnceAndEvents()
    {
        var calls = new List<string>();
        var engine = new WorkflowEngine().Register(Workflow.Create<JsonElement, string>("report", (ctx, input) =>
        {
            lock (calls) calls.Add($"{ctx.TriggeredBy}:{input.GetRawText()}");
            return Task.FromResult("ok");
        }));

        var cron = await engine.AddAutomationAsync(Automation.Create("Informe diario", new CronTrigger("0 9 * * *", "America/Santiago"), "report", new { tipo = "diario" }), TestContext.Current.CancellationToken);
        var once = await engine.AddAutomationAsync(Automation.Create("Recordatorio", new OnceTrigger(DateTimeOffset.UtcNow.AddMinutes(30)), "report", new { tipo = "una-vez" }), TestContext.Current.CancellationToken);
        await engine.AddAutomationAsync(new Automation { Id = "on-paid", Name = "Pagos", Trigger = new EventTrigger("order.updated", new Dictionary<string, string> { ["status"] = "paid" }), Workflow = "report" }, TestContext.Current.CancellationToken);

        Assert.Equal(9, TimeZoneInfo.ConvertTime(cron.NextRunAt!.Value, TimeZoneInfo.FindSystemTimeZoneById("America/Santiago")).Hour);

        await engine.TickAsync(DateTimeOffset.UtcNow.AddMinutes(1), TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();
        Assert.Empty(calls);

        await engine.TickAsync(once.NextRunAt!.Value.AddSeconds(1), TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();
        Assert.Equal(["schedule:" + once.Id + ":{\"tipo\":\"una-vez\"}"], calls);
        var afterOnce = (await engine.ListAutomationsAsync(TestContext.Current.CancellationToken)).Single(a => a.Id == once.Id);
        Assert.False(afterOnce.Enabled);
        Assert.Null(afterOnce.NextRunAt);

        await engine.TickAsync(cron.NextRunAt!.Value, TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();
        Assert.Contains("schedule:" + cron.Id + ":{\"tipo\":\"diario\"}", calls);
        var afterCron = (await engine.ListAutomationsAsync(TestContext.Current.CancellationToken)).Single(a => a.Id == cron.Id);
        // Se compara en hora local de Santiago: entre dos ejecuciones puede haber un cambio de horario (23 h o 25 h).
        var santiago = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var nextLocal = TimeZoneInfo.ConvertTime(afterCron.NextRunAt!.Value, santiago);
        Assert.Equal(TimeZoneInfo.ConvertTime(cron.NextRunAt.Value, santiago).Date.AddDays(1), nextLocal.Date);
        Assert.Equal(9, nextLocal.Hour);

        await engine.SendEventAsync("order.updated", new { status = "pending" }, cancellationToken: TestContext.Current.CancellationToken);
        await engine.SendEventAsync("order.updated", new { status = "paid", id = 7 }, cancellationToken: TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();
        Assert.Contains("event:order.updated:{\"status\":\"paid\",\"id\":7}", calls);
        Assert.DoesNotContain(calls, c => c.Contains("pending"));
    }

    [Fact]
    public async Task WebhooksVerifySecretsAndSignatures()
    {
        var engine = new WorkflowEngine().Register(Workflow.Create<JsonElement, string>("hook", (_, input) => Task.FromResult(input.GetProperty("event").GetString()!)));
        await engine.AddAutomationAsync(new Automation { Id = "github", Name = "GitHub", Trigger = new WebhookTrigger("s3cr3t"), Workflow = "hook" }, TestContext.Current.CancellationToken);
        var body = """{"event":"push"}"""u8.ToArray();
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("s3cr3t"u8, body));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.TriggerWebhookAsync("github", body, signatureHeader: "sha256=00", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.TriggerWebhookAsync("github", body, cancellationToken: TestContext.Current.CancellationToken));
        var run = await engine.TriggerWebhookAsync("github", body, signatureHeader: signature, cancellationToken: TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();

        Assert.Equal("push", (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.GetOutput<string>());
        Assert.Equal("webhook:github", run.TriggeredBy);
    }

    [Fact]
    public async Task WorkflowsRunAgentsAndOrchestrations()
    {
        var writer = new MockLanguageModel().ReturnsText("Resumen del día");
        var agent = new ToolLoopAgent(new AgentSettings { Name = "redactor", Model = writer });
        var engine = new WorkflowEngine().Register(Workflow.Create<string, string>("digest", async (ctx, topic) =>
        {
            var news = await ctx.StepAsync("fetch", _ => Task.FromResult($"noticias de {topic}"));
            var summary = await ctx.RunAgentAsync(agent, $"Resume: {news}");
            var shout = await ctx.RunAsync(OrchestrationNodes.Function("upper", s => s.ToUpperInvariant()), summary);
            return shout;
        }));

        var run = await engine.StartAsync("digest", "IA", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("RESUMEN DEL DÍA", run.GetOutput<string>());
        Assert.Equal("Resume: noticias de IA", writer.Requests[0].Messages[^1].Text);
        Assert.True(run.Steps.ContainsKey("agent:redactor"));
    }

    [Fact]
    public async Task AgentsScheduleAndManageTasks()
    {
        var notified = new List<AgentTaskResult>();
        var worker = new MockLanguageModel().ReturnsText("Precios revisados: sin cambios.");
        var engine = new WorkflowEngine().AddAgentTasks(new AgentTaskOptions
        {
            Agent = _ => new ToolLoopAgent(new AgentSettings { Name = "worker", Model = worker }),
            OnCompleted = (r, _, _) => { notified.Add(r); return Task.CompletedTask; },
            DefaultTimeZone = "America/Santiago",
        });
        var tools = AgentTasks.CreateTools(engine, new AgentTaskOptions { Agent = _ => null!, MinimumInterval = TimeSpan.FromMinutes(15) });
        var assistant = new MockLanguageModel()
            .ReturnsToolCall("schedule_task", new { name = "Revisar precios", prompt = "Revisa los precios de la competencia", cron = "0 9 * * 1-5" })
            .ReturnsToolCall("schedule_task", new { name = "Spam", prompt = "x", cron = "* * * * *" })
            .ReturnsToolCall("list_scheduled_tasks", new { })
            .ReturnsText("Programado.");

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = assistant,
            Prompt = "Revisa los precios cada día hábil a las 9",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(5),
            Context = new Dictionary<string, object?> { ["userId"] = "ana" },
        });

        Assert.False(result.Steps[0].ToolResults[0].IsError);
        Assert.True(result.Steps[1].ToolResults[0].IsError); // cada minuto: demasiado frecuente
        var listed = result.Steps[2].ToolResults[0].Output;
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Contains("0 9 * * 1-5", listed[0].GetProperty("schedule").GetString());

        var automation = (await engine.ListAutomationsAsync(TestContext.Current.CancellationToken)).Single();
        Assert.Equal("ana", automation.Owner);
        var run = await engine.RunAutomationNowAsync(automation.Id, TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();

        Assert.Equal("Precios revisados: sin cambios.", (await engine.GetRunAsync(run.Id, TestContext.Current.CancellationToken))!.GetOutput<string>());
        Assert.Equal(("Revisar precios", "ana"), (notified.Single().Task, notified.Single().Owner));

        // Otro usuario no ve ni puede cancelar la tarea.
        var other = new MockLanguageModel().ReturnsToolCall("cancel_scheduled_task", new { id = automation.Id }).ReturnsText("x");
        var denied = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = other,
            Prompt = "x",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(3),
            Context = new Dictionary<string, object?> { ["userId"] = "pedro" },
        });
        Assert.True(denied.Steps[0].ToolResults[0].IsError);
        Assert.Single(await engine.ListAutomationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HttpEndpointsDriveWorkflows()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAiSdkWorkflows(w =>
        {
            w.AddWorkflow(ApprovalEngine().Workflows.Single());
            w.AddAutomation(new Automation { Id = "crm", Name = "CRM", Trigger = new WebhookTrigger("clave"), Workflow = "approval" });
            w.TickInterval = TimeSpan.FromMilliseconds(50);
        });
        await using var app = builder.Build();
        app.MapWorkflows();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();
        var engine = app.Services.GetRequiredService<WorkflowEngine>();

        var start = await client.PostAsJsonAsync("/workflows/approval/start?wait=true", new { id = "W-1", amount = 2000 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;
        Assert.Equal("WaitingForEvent", (await client.GetFromJsonAsync<JsonElement>($"/workflows/runs/{runId}", cancellationToken: TestContext.Current.CancellationToken)).GetProperty("status").GetString());

        await client.PostAsJsonAsync("/workflows/events/order.approval?key=W-1", new { approved = true }, cancellationToken: TestContext.Current.CancellationToken);
        await engine.WhenIdleAsync();
        var run = await client.GetFromJsonAsync<JsonElement>($"/workflows/runs/{runId}", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal("aprobado por gerente", run.GetProperty("output").GetString());

        // Webhook: la automatización declarada en código se registró al arrancar.
        for (var i = 0; i < 40 && (await engine.ListAutomationsAsync(TestContext.Current.CancellationToken)).Count == 0; i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        var automationsResponse = await client.GetAsync("/workflows/automations", TestContext.Current.CancellationToken);
        var automationsJson = await automationsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(automationsResponse.IsSuccessStatusCode, automationsJson);
        Assert.Contains("\"kind\":\"webhook\"", automationsJson);
        var bad = new HttpRequestMessage(HttpMethod.Post, "/workflows/hooks/crm") { Content = new StringContent("""{"id":"W-2","amount":5}""", Encoding.UTF8, "application/json") };
        bad.Headers.Add("X-Webhook-Secret", "otra");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bad, TestContext.Current.CancellationToken)).StatusCode);
        var good = new HttpRequestMessage(HttpMethod.Post, "/workflows/hooks/crm") { Content = new StringContent("""{"id":"W-2","amount":5}""", Encoding.UTF8, "application/json") };
        good.Headers.Add("X-Webhook-Secret", "clave");
        var hook = await client.SendAsync(good, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, hook.StatusCode);
        await engine.WhenIdleAsync();
        var hookRun = (await hook.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("runId").GetString()!;
        Assert.Equal("aprobado automáticamente", (await engine.GetRunAsync(hookRun, TestContext.Current.CancellationToken))!.GetOutput<string>());
    }
}
