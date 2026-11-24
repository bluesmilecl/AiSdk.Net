using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AiSdk.AspNetCore;
using AiSdk.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.Tests;

/// <summary>Reloj manual para el motor (las esperas reales siguen usando el temporizador del sistema).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Almacén que cuenta las consultas sin límite (para comprobar que los ciclos no recorren el historial).</summary>
internal sealed class CountingStore(IWorkflowStore inner) : IWorkflowStore
{
    public int FullListings;

    public Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default) => inner.SaveRunAsync(run, cancellationToken);

    public Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default) => inner.GetRunAsync(id, cancellationToken);

    public Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref FullListings);
        return inner.ListRunsAsync(status, workflowName, limit, cancellationToken);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListDueRunsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) => inner.ListDueRunsAsync(now, limit, cancellationToken);

    public Task<IReadOnlyList<WorkflowRun>> ListRunsWaitingForEventAsync(string eventName, string? key, int limit = 100, CancellationToken cancellationToken = default) => inner.ListRunsWaitingForEventAsync(eventName, key, limit, cancellationToken);

    public Task<int> DeleteFinishedRunsAsync(DateTimeOffset finishedBefore, int limit = 1000, CancellationToken cancellationToken = default) => inner.DeleteFinishedRunsAsync(finishedBefore, limit, cancellationToken);

    public Task<Automation> SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default) => inner.SaveAutomationAsync(automation, cancellationToken);

    public Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default) => inner.GetAutomationAsync(id, cancellationToken);

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default) => inner.ListAutomationsAsync(cancellationToken);

    public Task<IReadOnlyList<Automation>> ListDueAutomationsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) => inner.ListDueAutomationsAsync(now, limit, cancellationToken);

    public Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAutomationAsync(id, cancellationToken);
}

public class WorkflowSecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Workflow<JsonElement, string> Echo(string name, List<string>? calls = null) =>
        Workflow.Create<JsonElement, string>(name, (ctx, input) =>
        {
            if (calls is not null)
            {
                lock (calls) calls.Add($"{name}:{ctx.TriggeredBy}");
            }

            return Task.FromResult(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("event", out var e) ? e.GetString()! : "ok");
        });

    // ------------------------------------------------------------------
    // Hallazgo 6: webhooks
    // ------------------------------------------------------------------

    [Fact]
    public async Task WebhooksRequireASecretUnlessExplicitlyUnauthenticated()
    {
        var engine = new WorkflowEngine().Register(Echo("hook"));

        var missing = await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAutomationAsync(new Automation { Id = "h1", Name = "h", Trigger = new WebhookTrigger(), Workflow = "hook" }, Ct));
        Assert.Contains("AllowUnauthenticated", missing.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAutomationAsync(new Automation { Id = "h2", Name = "h", Trigger = new WebhookTrigger("corto"), Workflow = "hook" }, Ct));

        // Un webhook guardado sin secreto (p. ej. por una versión anterior) se rechaza: falla cerrado.
        await engine.Store.SaveAutomationAsync(new Automation { Id = "legacy", Name = "h", Trigger = new WebhookTrigger(), Workflow = "hook" }, Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.TriggerWebhookAsync("legacy", """{"event":"x"}"""u8.ToArray(), cancellationToken: Ct));

        var open = await engine.AddAutomationAsync(Automation.Create("Abierto", new WebhookTrigger { AllowUnauthenticated = true }, "hook", new { }), Ct);
        Assert.Matches("^abierto-[0-9a-f]{32}$", open.Id); // 128 bits aleatorios: la URL no se adivina
        var run = await engine.TriggerWebhookAsync(open.Id, """{"event":"x"}"""u8.ToArray(), cancellationToken: Ct);
        Assert.Equal("webhook:" + open.Id, run.TriggeredBy);
    }

    [Fact]
    public async Task SignedWebhooksCannotBeReplayed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var engine = new WorkflowEngine(options: new WorkflowEngineOptions { TimeProvider = clock }).Register(Echo("hook"));
        var secret = WebhookSignature.CreateSecret();
        await engine.AddAutomationAsync(new Automation { Id = "pagos", Name = "Pagos", Trigger = new WebhookTrigger(secret), Workflow = "hook" }, Ct);
        var body = """{"event":"pago"}"""u8.ToArray();

        WebhookRequest Signed(DateTimeOffset at, string? delivery = null)
        {
            var timestamp = at.ToUnixTimeSeconds();
            return new WebhookRequest(body) { Signature = WebhookSignature.Sign(secret, timestamp, body), Timestamp = timestamp.ToString(CultureInfo.InvariantCulture), DeliveryId = delivery };
        }

        var captured = Signed(clock.Now);
        var first = await engine.TriggerWebhookAsync("pagos", captured, Ct);

        // El atacante repite la petición capturada, también cambiando el id de entrega: no se crea otra ejecución.
        Assert.Equal(first.Id, (await engine.TriggerWebhookAsync("pagos", captured, Ct)).Id);
        Assert.Equal(first.Id, (await engine.TriggerWebhookAsync("pagos", captured with { DeliveryId = "otro" }, Ct)).Id);

        // La firma antigua (solo cuerpo, sin marca de tiempo) ya no basta.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.TriggerWebhookAsync("pagos", body, signatureHeader: WebhookSignature.SignBody(secret, body), cancellationToken: Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.TriggerWebhookAsync("pagos", captured with { Timestamp = (clock.Now.ToUnixTimeSeconds() + 1).ToString(CultureInfo.InvariantCulture) }, Ct));

        // Pasada la tolerancia, la petición capturada se rechaza.
        clock.Now += TimeSpan.FromMinutes(6);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.TriggerWebhookAsync("pagos", captured, Ct));

        // Reintento legítimo del emisor: nueva marca de tiempo y mismo id de entrega → la misma ejecución.
        var delivery = await engine.TriggerWebhookAsync("pagos", Signed(clock.Now, "entrega-1"), Ct);
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal(delivery.Id, (await engine.TriggerWebhookAsync("pagos", Signed(clock.Now, "entrega-1"), Ct)).Id);

        await engine.WhenIdleAsync();
        Assert.Equal(2, (await engine.Store.ListRunsAsync(cancellationToken: Ct)).Count);
    }

    [Fact]
    public async Task WebhookEndpointForwardsTimestampAndDeliveryHeaders()
    {
        var secret = WebhookSignature.CreateSecret();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAiSdkWorkflows(w =>
        {
            w.AddWorkflow(Echo("hook"));
            w.AddAutomation(new Automation { Id = "crm", Name = "CRM", Trigger = new WebhookTrigger(secret), Workflow = "hook" });
        });
        await using var app = builder.Build();
        app.MapWorkflowWebhooks();
        await app.StartAsync(Ct);
        var engine = app.Services.GetRequiredService<WorkflowEngine>();
        for (var i = 0; i < 200 && await engine.Store.GetAutomationAsync("crm", Ct) is null; i++) await Task.Delay(10, Ct);
        var client = app.GetTestClient();
        var body = """{"event":"lead"}""";

        HttpRequestMessage Request(bool withTimestamp)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var request = new HttpRequestMessage(HttpMethod.Post, "/workflows/hooks/crm") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Sign(secret, timestamp, Encoding.UTF8.GetBytes(body)));
            request.Headers.Add(WebhookSignature.DeliveryIdHeader, "d-1");
            if (withTimestamp)
            {
                request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            }

            return request;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(withTimestamp: false), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(Request(withTimestamp: true), Ct)).StatusCode);
        await engine.WhenIdleAsync();
    }

    // ------------------------------------------------------------------
    // Hallazgo 7: resiliencia del programador
    // ------------------------------------------------------------------

    [Fact]
    public async Task AFailingAutomationDoesNotBlockOthersAndIsEventuallyDisabled()
    {
        var calls = new List<string>();
        var store = new InMemoryWorkflowStore();
        var before = new WorkflowEngine(store).Register(Echo("retirado"), Echo("vigente", calls));
        var broken = await before.AddAutomationAsync(Automation.Create("Rota", new IntervalTrigger(TimeSpan.FromMinutes(5)), "retirado", new { }), Ct);
        var healthy = await before.AddAutomationAsync(Automation.Create("Sana", new IntervalTrigger(TimeSpan.FromMinutes(5)), "vigente", new { }), Ct);

        // Nuevo despliegue: el workflow "retirado" ya no existe.
        var engine = new WorkflowEngine(store, options: new WorkflowEngineOptions { MaxAutomationFailures = 2, AutomationRetryDelay = TimeSpan.FromMinutes(1) })
            .Register(Echo("vigente", calls));
        var t = broken.NextRunAt!.Value.AddMinutes(1);
        await engine.TickAsync(t, Ct);
        await engine.WhenIdleAsync();

        Assert.Equal(["vigente:schedule:" + healthy.Id], calls); // la automatización sana se disparó igual
        var failed = (await store.GetAutomationAsync(broken.Id, Ct))!;
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Contains("no está registrado", failed.LastError);
        Assert.Equal(t + TimeSpan.FromMinutes(1), failed.NextRunAt); // espera antes de reintentar
        Assert.True(failed.Enabled);

        await engine.TickAsync(t.AddMinutes(2), Ct);
        var disabled = (await store.GetAutomationAsync(broken.Id, Ct))!;
        Assert.False(disabled.Enabled);
        Assert.Null(disabled.NextRunAt);
    }

    [Fact]
    public async Task UnknownTimeZonesFailAtCreationAndNeverCreateRuns()
    {
        var store = new InMemoryWorkflowStore();
        var engine = new WorkflowEngine(store).Register(Echo("report"));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => engine.AddAutomationAsync(Automation.Create("x", new CronTrigger("0 9 * * *", "Marte/Olympus_Mons"), "report", new { }), Ct));
        Assert.Contains("Marte/Olympus_Mons", error.Message);

        // Automatización guardada en otro sistema (o con una zona que dejó de existir).
        var now = DateTimeOffset.UtcNow;
        await store.SaveAutomationAsync(new Automation { Id = "tz", Name = "tz", Trigger = new CronTrigger("* * * * *", "Marte/Olympus_Mons"), Workflow = "report", NextRunAt = now.AddMinutes(-1) }, Ct);
        await engine.TickAsync(now, Ct);
        await engine.TickAsync(now.AddSeconds(15), Ct);
        await engine.WhenIdleAsync();

        Assert.Empty(await store.ListRunsAsync(cancellationToken: Ct)); // antes: una ejecución nueva en cada ciclo
        var stored = (await store.GetAutomationAsync("tz", Ct))!;
        Assert.Equal(1, stored.ConsecutiveFailures);
        Assert.Equal(now + TimeSpan.FromMinutes(1), stored.NextRunAt);
    }

    // ------------------------------------------------------------------
    // Hallazgo 8: cancelación y concurrencia
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelSignalsTheRunningWorkflowAndIsFinal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = 0;
        var engine = new WorkflowEngine().Register(Workflow.Create<int, int>("slow", async (ctx, _) =>
        {
            await ctx.StepAsync("wait", async ct =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return 1;
            });
            return await ctx.StepAsync("after", _ => Task.FromResult(++after));
        }));

        var running = engine.StartAsync("slow", 0, runId: "c1", cancellationToken: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(WorkflowStatus.Cancelled, (await engine.CancelAsync("c1", Ct)).Status);

        Assert.Equal(WorkflowStatus.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status); // el paso recibió la cancelación
        Assert.Equal(WorkflowStatus.Cancelled, (await engine.GetRunAsync("c1", Ct))!.Status);
        Assert.Equal(0, after);
        Assert.Equal(0, engine.RunLockCount); // los semáforos por ejecución se liberan
    }

    [Fact]
    public async Task AWorkflowThatIgnoresCancellationCannotOverwriteIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = 0;
        var engine = new WorkflowEngine().Register(Workflow.Create<int, int>("stubborn", async (ctx, _) =>
        {
            await ctx.StepAsync("ignores-token", async _ =>
            {
                entered.TrySetResult();
                await release.Task; // no mira el token
                return 1;
            });
            return await ctx.StepAsync("after", _ => Task.FromResult(++after));
        }));

        var running = engine.StartAsync("stubborn", 0, runId: "c2", cancellationToken: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await engine.CancelAsync("c2", Ct);
        release.SetResult();

        Assert.Equal(WorkflowStatus.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Status);
        Assert.Equal(WorkflowStatus.Cancelled, (await engine.GetRunAsync("c2", Ct))!.Status); // antes: el siguiente guardado lo pisaba
        Assert.Equal(0, after);
    }

    [Fact]
    public async Task ReplicasSharingAStoreFireEachOccurrenceOnce()
    {
        var calls = new List<string>();
        var store = new InMemoryWorkflowStore();
        var a = new WorkflowEngine(store).Register(Echo("report", calls));
        var b = new WorkflowEngine(store).Register(Echo("report", calls));
        var automation = await a.AddAutomationAsync(Automation.Create("Informe", new IntervalTrigger(TimeSpan.FromHours(1)), "report", new { }), Ct);

        var due = automation.NextRunAt!.Value;
        await Task.WhenAll(a.TickAsync(due, Ct), b.TickAsync(due, Ct), a.TickAsync(due, Ct), b.TickAsync(due, Ct));
        await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());

        Assert.Single(calls);
        Assert.Single(await store.ListRunsAsync(cancellationToken: Ct));
        Assert.Equal(due + TimeSpan.FromHours(1), (await store.GetAutomationAsync(automation.Id, Ct))!.NextRunAt);
    }

    [Fact]
    public async Task ReplicasDoNotStealLeasedRunsButTakeOverAbandonedOnes()
    {
        var store = new InMemoryWorkflowStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepsOnA = 0;
        var a = new WorkflowEngine(store).Register(Workflow.Create<int, string>("job", async (ctx, _) =>
        {
            await ctx.StepAsync("work", async _ =>
            {
                entered.TrySetResult();
                await release.Task; // réplica A colgada (pausa larga, red caída...)
                return "a";
            });
            Interlocked.Increment(ref stepsOnA);
            return "terminado en A";
        }));
        var clockB = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var stepsOnB = 0;
        var b = new WorkflowEngine(store, options: new WorkflowEngineOptions { TimeProvider = clockB }).Register(Workflow.Create<int, string>("job", async (ctx, _) =>
        {
            await ctx.StepAsync("work", _ => Task.FromResult("b"));
            Interlocked.Increment(ref stepsOnB);
            return "terminado en B";
        }));

        var onA = a.StartAsync("job", 0, runId: "j1", cancellationToken: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Mientras el reclamo de A está vigente, B no la toca (antes RecoverAsync la volvía a ejecutar).
        await b.RecoverAsync(Ct);
        await b.TickAsync(cancellationToken: Ct);
        await b.WhenIdleAsync();
        Assert.Equal(0, stepsOnB);

        // A deja de renovar el reclamo: al vencer, B la retoma.
        clockB.Now += TimeSpan.FromMinutes(10);
        await b.TickAsync(cancellationToken: Ct);
        await b.WhenIdleAsync();
        Assert.Equal("terminado en B", (await store.GetRunAsync("j1", Ct))!.GetOutput<string>());

        // A despierta: su guardado se rechaza y no pisa el resultado.
        release.SetResult();
        var fromA = await onA.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("terminado en B", fromA.GetOutput<string>());
        Assert.Equal(0, stepsOnA);
    }

    [Fact]
    public async Task StoresRejectStaleWritesAcrossInstances()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-wf-cas-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new FileSystemWorkflowStore(dir);
            using var second = new FileSystemWorkflowStore(dir); // otro proceso sobre el mismo directorio
            foreach (var store in new IWorkflowStore[] { new InMemoryWorkflowStore(), first })
            {
                var other = store == first ? second : store;
                await store.SaveRunAsync(new WorkflowRun { Id = "r1", WorkflowName = "w", Input = JsonSerializer.SerializeToElement(new { }) }, Ct);
                var copyA = (await store.GetRunAsync("r1", Ct))!;
                var copyB = (await other.GetRunAsync("r1", Ct))!;
                copyA.Status = WorkflowStatus.Cancelled;
                await store.SaveRunAsync(copyA, Ct);
                copyB.Status = WorkflowStatus.Running;
                await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => other.SaveRunAsync(copyB, Ct));
                await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => other.SaveRunAsync(new WorkflowRun { Id = "r1", WorkflowName = "w", Input = copyA.Input }, Ct));
                Assert.Equal(WorkflowStatus.Cancelled, (await other.GetRunAsync("r1", Ct))!.Status);
            }

            // Escrituras en paralelo desde dos instancias: ninguna actualización se pierde.
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                var store = i % 2 == 0 ? first : second;
                while (true)
                {
                    var run = (await store.GetRunAsync("r1", Ct))!;
                    run.Logs.Add($"línea {i}");
                    try
                    {
                        await store.SaveRunAsync(run, Ct);
                        return;
                    }
                    catch (WorkflowConcurrencyException)
                    {
                    }
                }
            }, Ct)));
            Assert.Equal(20, (await first.GetRunAsync("r1", Ct))!.Logs.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------
    // Hallazgo 9: crecimiento sin límite
    // ------------------------------------------------------------------

    [Fact]
    public async Task TicksAndEventsUseIndexedQueriesInsteadOfListingEverything()
    {
        var store = new CountingStore(new InMemoryWorkflowStore());
        var engine = new WorkflowEngine(store).Register(Workflow.Create<string, string>("approval", async (ctx, id) =>
            (await ctx.WaitForEventAsync<JsonElement?>("approved", key: id))?.GetProperty("by").GetString() ?? "vencido"));
        for (var i = 0; i < 5; i++)
        {
            await engine.StartAsync("approval", $"P-{i}", cancellationToken: Ct);
        }

        await engine.TickAsync(cancellationToken: Ct);
        Assert.Equal(["P-3"], (await engine.SendEventAsync("approved", new { by = "ana" }, key: "P-3", cancellationToken: Ct)).Select(id => engine.GetRunAsync(id, Ct).Result!.Input.GetString()));
        await engine.RecoverAsync(Ct);
        await engine.WhenIdleAsync();

        Assert.Equal(0, store.FullListings);
        Assert.Equal(0, engine.RunLockCount);
    }

    [Fact]
    public async Task FinishedRunsArePurgedAndTheInMemoryStoreIsBounded()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-wf-purge-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var disk = new FileSystemWorkflowStore(dir);
            foreach (IWorkflowStore store in new IWorkflowStore[] { new InMemoryWorkflowStore(), disk })
            {
                var engine = new WorkflowEngine(store, options: new WorkflowEngineOptions { TimeProvider = clock, RunRetention = TimeSpan.FromDays(30) })
                    .Register(Echo("quick"), Workflow.Create<int, string>("waits", async (ctx, _) => await ctx.WaitForEventAsync<string>("never") ?? "x"));
                var done = await engine.StartAsync("quick", new { }, cancellationToken: Ct);
                var waiting = await engine.StartAsync("waits", 1, cancellationToken: Ct);

                clock.Now += TimeSpan.FromDays(31);
                await engine.TickAsync(cancellationToken: Ct); // la retención se aplica en el ciclo
                Assert.Null(await store.GetRunAsync(done.Id, Ct));
                Assert.NotNull(await store.GetRunAsync(waiting.Id, Ct)); // las activas no se borran
                Assert.Equal(0, await engine.PurgeRunsAsync(clock.Now, Ct));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        var bounded = new InMemoryWorkflowStore(maxFinishedRuns: 10);
        var small = new WorkflowEngine(bounded).Register(Echo("quick"));
        string? last = null;
        for (var i = 0; i < 25; i++)
        {
            last = (await small.StartAsync("quick", new { }, cancellationToken: Ct)).Id;
        }

        Assert.InRange((await bounded.ListRunsAsync(limit: 100, cancellationToken: Ct)).Count, 1, 10);
        Assert.NotNull(await bounded.GetRunAsync(last!, Ct));
    }

    [Fact]
    public async Task FileStoresFromPreviousVersionsGetAnActiveRunIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-wf-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Directorio de 0.1: solo runs/ y automations/, sin versión ni índice.
            Directory.CreateDirectory(Path.Combine(dir, "runs"));
            await File.WriteAllTextAsync(Path.Combine(dir, "runs", "old.json"), $$"""
                {"id":"old","workflowName":"w","status":"Sleeping","input":{},"resumeAt":"{{DateTimeOffset.UtcNow.AddMinutes(-1):O}}","createdAt":"{{DateTimeOffset.UtcNow.AddDays(-1):O}}","updatedAt":"{{DateTimeOffset.UtcNow.AddDays(-1):O}}"}
                """, Ct);

            using var store = new FileSystemWorkflowStore(dir);
            var due = Assert.Single(await store.ListDueRunsAsync(DateTimeOffset.UtcNow, cancellationToken: Ct));
            Assert.Equal("old", due.Id);
            await store.SaveRunAsync(due, Ct); // versión 0 → 1 sin conflicto
            Assert.Equal(1, (await store.GetRunAsync("old", Ct))!.Version);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------
    // Hallazgo 10: tareas programadas por agentes
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("0,16-59 * * * *")] // antes pasaba si se creaba a las hh:59 (primer intervalo de 16 min)
    [InlineData("5,55 0,23 * * *")] // intervalos de 50 min salvo 23:55 → 00:05 (10 min): antes casi siempre pasaba
    public void FrequencyIsCheckedOverAFullWeek(string expression) =>
        Assert.Throws<ArgumentException>(() => AgentTasks.ValidateFrequency(new CronTrigger(expression), TimeSpan.FromMinutes(15)));

    [Fact]
    public void ReasonableCronExpressionsPass()
    {
        AgentTasks.ValidateFrequency(new CronTrigger("0 9 * * 1-5", "America/Santiago"), TimeSpan.FromMinutes(15));
        AgentTasks.ValidateFrequency(new CronTrigger("*/15 * * * *"), TimeSpan.FromMinutes(15));
        AgentTasks.ValidateFrequency(new CronTrigger("0 0 1 1 *"), TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task TaskToolsFailClosedWithoutAnOwnerAndTheRunGetsTheOwnerContext()
    {
        string? seenOwner = null;
        var whoami = Tool.Create<JsonElement, string>("whoami", "Devuelve el usuario.", (_, ctx, _) =>
        {
            seenOwner = ctx.Context?["userId"]?.ToString();
            return Task.FromResult("ok");
        });
        var worker = new MockLanguageModel().ReturnsToolCall("whoami", new { }).ReturnsText("listo");
        var options = new AgentTaskOptions { Agent = _ => new ToolLoopAgent(new AgentSettings { Name = "worker", Model = worker, Tools = [whoami] }) };
        var engine = new WorkflowEngine().AddAgentTasks(options);
        var tools = AgentTasks.CreateTools(engine, options);

        // Sin usuario en el contexto: antes todos compartían el dueño null y veían las tareas de los demás.
        var anonymous = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = new MockLanguageModel()
                .ReturnsToolCall("schedule_task", new { name = "x", prompt = "x", cron = "0 9 * * 1-5" })
                .ReturnsToolCall("list_scheduled_tasks", new { })
                .ReturnsText("x"),
            Prompt = "x",
            Tools = tools,
            StopWhen = StopCondition.StepCountIs(3),
        });
        Assert.True(anonymous.Steps[0].ToolResults[0].IsError);
        Assert.True(anonymous.Steps[1].ToolResults[0].IsError);
        Assert.Empty(await engine.ListAutomationsAsync(Ct));

        var automation = await engine.AddAutomationAsync(
            Automation.Create("t", new IntervalTrigger(TimeSpan.FromHours(1)), AgentTasks.WorkflowName, new AgentTaskInput("t", "¿Quién soy?", "ana")) with { Owner = "ana" },
            Ct);
        await engine.RunAutomationNowAsync(automation.Id, Ct);
        await engine.WhenIdleAsync();
        Assert.Equal("ana", seenOwner); // el agente programado se ejecuta en nombre de su dueño
    }
}
