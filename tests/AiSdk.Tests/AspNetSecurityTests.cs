using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiSdk.Artifacts;
using AiSdk.AspNetCore;
using AiSdk.Skills;
using AiSdk.Workflows;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiSdk.Tests;

/// <summary>Autenticación de prueba: la cabecera <c>X-Test-User</c> identifica al usuario y <c>X-Test-Roles</c> sus roles.</summary>
internal static class TestAuth
{
    public const string Scheme = "Test";

    public static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, null);
        builder.Services.AddAuthorization();
        return builder;
    }

    public static HttpClient Client(WebApplication app, string? user = null, string? roles = null)
    {
        var client = app.GetTestClient();
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", user);
        }

        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        }

        return client;
    }

    public static StringContent Json(string json, string mediaType = "application/json") => new(json, Encoding.UTF8, mediaType);

    private sealed class Handler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-User"].ToString() is not { Length: > 0 } user)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
            claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, TestAuth.Scheme)), TestAuth.Scheme)));
        }
    }
}

public class ArtifactSecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WebApplication> StartAsync(IArtifactStore store, ArtifactEndpointOptions? options = null)
    {
        var builder = TestAuth.CreateBuilder();
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.MapArtifacts(options: options);
        await app.StartAsync(Ct);
        return app;
    }

    private static async Task<InMemoryArtifactStore> SharedConversationStoreAsync()
    {
        // ana y bob eligieron (o adivinaron) el mismo id de conversación en el cliente.
        var store = new InMemoryArtifactStore();
        await store.SaveAsync(new Artifact { Id = "ana-doc", Title = "Sueldos", Type = ArtifactTypes.Markdown, Content = "secreto de ana", ConversationId = "c1", Owner = "ana" }, Ct);
        await store.SaveAsync(new Artifact { Id = "bob-doc", Title = "Notas", Type = ArtifactTypes.Markdown, Content = "de bob", ConversationId = "c1", Owner = "bob" }, Ct);
        await store.SaveAsync(new Artifact { Id = "sin-dueno", Title = "Antiguo", Type = ArtifactTypes.Markdown, Content = "sin dueño", ConversationId = "c1" }, Ct);
        return store;
    }

    [Fact]
    public async Task EndpointsRequireAuthenticationByDefault()
    {
        await using var app = await StartAsync(await SharedConversationStoreAsync());
        var anonymous = TestAuth.Client(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/artifacts/ana-doc", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/artifacts?conversationId=c1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/artifacts/ana-doc/raw", Ct)).StatusCode);
    }

    [Fact]
    public async Task UsersCannotReadOrListOtherUsersArtifacts()
    {
        await using var app = await StartAsync(await SharedConversationStoreAsync());
        var ana = TestAuth.Client(app, "ana");
        var bob = TestAuth.Client(app, "bob");

        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/artifacts/ana-doc", Ct)).StatusCode);
        foreach (var url in new[] { "/artifacts/ana-doc", "/artifacts/ana-doc/raw", "/artifacts/ana-doc/v/1", "/artifacts/ana-doc/versions", "/artifacts/sin-dueno" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(url, Ct)).StatusCode);
        }

        // El listado por conversación (id elegido por el cliente) solo devuelve lo propio, y sin conversación no lista todo.
        var byConversation = await bob.GetFromJsonAsync<JsonElement>("/artifacts?conversationId=c1", Ct);
        Assert.Equal(["bob-doc"], byConversation.EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        var mine = await ana.GetFromJsonAsync<JsonElement>("/artifacts", Ct);
        Assert.Equal(["ana-doc"], mine.EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.DoesNotContain("content", mine[0].EnumerateObject().Select(p => p.Name)); // solo metadatos
    }

    [Fact]
    public async Task AnonymousModeIsExplicitAndStillFailsClosed()
    {
        await using var open = await StartAsync(await SharedConversationStoreAsync(), new ArtifactEndpointOptions { AllowAnonymous = true });
        var anonymous = TestAuth.Client(open);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/artifacts/ana-doc", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/artifacts/sin-dueno", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync("/artifacts", Ct)).StatusCode);

        await using var custom = await StartAsync(await SharedConversationStoreAsync(), new ArtifactEndpointOptions
        {
            AllowAnonymous = true,
            CanAccess = (_, a) => ValueTask.FromResult(a.Owner is null),
        });
        var client = TestAuth.Client(custom);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/artifacts/sin-dueno", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/artifacts/ana-doc", Ct)).StatusCode);
    }

    [Fact]
    public async Task ActiveContentIsNeverServedInlineOnTheAppOrigin()
    {
        var store = new InMemoryArtifactStore();
        const string Xss = "<script>fetch('/api/secreto?c='+document.cookie)</script>";
        await store.SaveAsync(new Artifact { Id = "pagina", Title = "P", Type = ArtifactTypes.Html, Content = Xss, Owner = "ana" }, Ct);
        await store.SaveAsync(new Artifact { Id = "dibujo", Title = "D", Type = ArtifactTypes.Svg, Content = "<svg onload=\"alert(1)\"/>", Owner = "ana" }, Ct);
        await store.SaveAsync(new Artifact { Id = "notas", Title = "N", Type = ArtifactTypes.Markdown, Content = "# hola", Owner = "ana" }, Ct);
        await store.SaveAsync(new Artifact { Id = "archivo", Title = "A", Type = ArtifactTypes.File, Data = Encoding.UTF8.GetBytes(Xss), MediaType = "TEXT/HTML; charset=utf-8", Owner = "ana" }, Ct);
        await store.SaveAsync(new Artifact { Id = "raro", Title = "R", Type = ArtifactTypes.File, Data = [1, 2], MediaType = "no es un tipo", Owner = "ana" }, Ct);
        await using var app = await StartAsync(store);
        var ana = TestAuth.Client(app, "ana");

        foreach (var (url, fileName) in new[] { ("/artifacts/pagina/raw", "pagina.html"), ("/artifacts/dibujo/raw", "dibujo.svg"), ("/artifacts/archivo", "archivo.html"), ("/artifacts/archivo/raw", "archivo.html") })
        {
            var response = await ana.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(fileName, response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName);
            Assert.Equal(ArtifactRenderer.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }

        var markdown = await ana.GetAsync("/artifacts/notas/raw", Ct);
        Assert.Null(markdown.Content.Headers.ContentDisposition);
        Assert.Equal("text/markdown", markdown.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ArtifactRenderer.ContentSecurityPolicy, markdown.Headers.GetValues("Content-Security-Policy").Single());

        var invalid = await ana.GetAsync("/artifacts/raro", Ct);
        Assert.Equal("application/octet-stream", invalid.Content.Headers.ContentType!.MediaType);

        // La página renderizada (para el iframe) también corre en el sandbox.
        var rendered = await ana.GetAsync("/artifacts/pagina", Ct);
        Assert.Equal(ArtifactRenderer.ContentSecurityPolicy, rendered.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static Task<GenerateTextResult> RunToolsAsync(MockLanguageModel model, IReadOnlyList<Tool> tools, Dictionary<string, object?>? context) =>
        Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", Tools = tools, StopWhen = StopCondition.StepCountIs(5), Context = context });

    [Fact]
    public async Task ToolsFailClosedWithoutConversationOrUser()
    {
        var store = new InMemoryArtifactStore();
        await store.SaveAsync(new Artifact { Id = "ajeno", Title = "x", Type = ArtifactTypes.Markdown, Content = "secreto", ConversationId = "c1", Owner = "ana" }, Ct);
        var model = new MockLanguageModel()
            .ReturnsToolCall("list_artifacts", new { })
            .ReturnsToolCall("read_artifact", new { id = "ajeno" })
            .ReturnsToolCall("create_artifact", new { title = "Nuevo", type = "markdown", content = "x" })
            .ReturnsText("ok");

        var result = await RunToolsAsync(model, ArtifactTools.Create(store), context: null);

        Assert.All(result.Steps.Take(3), s => Assert.True(s.ToolResults[0].IsError));
        Assert.Single(await store.ListAsync(cancellationToken: Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ArtifactTools.SaveFileAsync(
            store, [1], "a.pdf", "application/pdf", null, new ToolExecutionContext { ToolCallId = "1", ToolName = "t", Messages = [] }, cancellationToken: Ct));

        // Modo sin ámbito: explícito, y solo ve artifacts sin dueño.
        var unscoped = new MockLanguageModel().ReturnsToolCall("list_artifacts", new { }).ReturnsText("ok");
        var listed = await RunToolsAsync(unscoped, ArtifactTools.Create(store, new ArtifactToolOptions { AllowUnscoped = true }), context: null);
        Assert.Equal(0, listed.Steps[0].ToolResults[0].Output.GetArrayLength());
    }

    [Fact]
    public async Task ToolsIsolateUsersThatShareAConversationId()
    {
        var store = new InMemoryArtifactStore();
        var tools = ArtifactTools.Create(store);
        var create = new MockLanguageModel().ReturnsToolCall("create_artifact", new { title = "Plan", type = "markdown", content = "plan de ana" }).ReturnsText("ok");
        await RunToolsAsync(create, tools, new() { ["conversationId"] = "c1", ["userId"] = "ana" });
        var saved = Assert.Single(await store.ListAsync(cancellationToken: Ct));
        Assert.Equal(("c1", "ana"), (saved.ConversationId, saved.Owner));

        var bob = new MockLanguageModel()
            .ReturnsToolCall("read_artifact", new { id = "plan" })
            .ReturnsToolCall("update_artifact", new { id = "plan", oldStr = "ana", newStr = "bob" })
            .ReturnsToolCall("list_artifacts", new { })
            .ReturnsText("ok");
        var result = await RunToolsAsync(bob, tools, new() { ["conversationId"] = "c1", ["userId"] = "bob" });

        Assert.True(result.Steps[0].ToolResults[0].IsError);
        Assert.True(result.Steps[1].ToolResults[0].IsError);
        Assert.Equal(0, result.Steps[2].ToolResults[0].Output.GetArrayLength());
        Assert.Equal("plan de ana", (await store.GetAsync("plan", cancellationToken: Ct))!.Content);
    }

    [Fact]
    public async Task FileSystemListingDoesNotLoadBinaries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aisdk-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileSystemArtifactStore(dir);
            await store.SaveAsync(new Artifact { Id = "grande", Title = "G", Type = ArtifactTypes.File, Data = new byte[1024], FileName = "g.bin", Owner = "ana" }, Ct);

            var listed = Assert.Single(await store.ListAsync(cancellationToken: Ct));
            Assert.Null(listed.Data);
            Assert.Equal("ana", listed.Owner);
            Assert.Equal(1024, (await store.GetAsync("grande", cancellationToken: Ct))!.Data!.Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class WorkflowEndpointSecurityTests
{
    private sealed record Order(string Id, decimal Amount);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WebApplication> StartAsync(WorkflowEndpointOptions? options = null)
    {
        var builder = TestAuth.CreateBuilder();
        builder.Services.AddAiSdkWorkflows(w =>
        {
            w.AddWorkflow(Workflow.Create<Order, string>("approval", async (ctx, order) =>
            {
                var decision = await ctx.WaitForEventAsync<JsonElement?>("order.approval", key: order.Id, timeout: TimeSpan.FromHours(48));
                return decision is { } d && d.GetProperty("approved").GetBoolean() ? "aprobado" : "rechazado";
            }));
            w.AddWorkflow(Workflow.Create<Order, string>("internal", (_, _) => Task.FromResult("interno")));
        });
        var app = builder.Build();
        app.MapWorkflows(options: options);
        await app.StartAsync(Ct);
        return app;
    }

    private static async Task<string> StartRunAsync(HttpClient client, string orderId)
    {
        var response = await client.PostAsync("/workflows/approval/start?wait=true", TestAuth.Json($$"""{"id":"{{orderId}}","amount":5000}"""), Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task EndpointsRequireAuthenticationButWebhooksStayReachable()
    {
        await using var app = await StartAsync();
        var anonymous = TestAuth.Client(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/workflows/approval/start", TestAuth.Json("{}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/workflows/runs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/workflows/events/order.approval?key=A", TestAuth.Json("""{"approved":true}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/workflows/automations", TestAuth.Json("{}"), Ct)).StatusCode);
        // Los webhooks se autentican con su secreto, no con el usuario.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync("/workflows/hooks/nada", TestAuth.Json("{}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task CrossSitePostsWithoutJsonContentTypeAreRejected()
    {
        await using var app = await StartAsync();
        var ana = TestAuth.Client(app, "ana");
        var runId = await StartRunAsync(ana, "A-1");

        // Un formulario de otro sitio puede enviar JSON válido como text/plain sin petición previa CORS.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await ana.PostAsync("/workflows/approval/start", TestAuth.Json("""{"id":"A-2","amount":1}""", "text/plain"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await ana.PostAsync("/workflows/events/order.approval?key=A-1", new FormUrlEncodedContent(new Dictionary<string, string> { ["approved"] = "true" }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await ana.PostAsync($"/workflows/runs/{runId}/cancel", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await ana.PostAsync("/workflows/automations", TestAuth.Json("{}", "text/plain"), Ct)).StatusCode);
        Assert.Equal("WaitingForEvent", (await ana.GetFromJsonAsync<JsonElement>($"/workflows/runs/{runId}", Ct)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task RunsAreOnlyVisibleToTheirOwner()
    {
        await using var app = await StartAsync();
        var ana = TestAuth.Client(app, "ana");
        var bob = TestAuth.Client(app, "bob");
        var runId = await StartRunAsync(ana, "A-1");

        var own = await ana.GetFromJsonAsync<JsonElement>($"/workflows/runs/{runId}", Ct);
        Assert.Equal("user:ana", own.GetProperty("triggeredBy").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/workflows/runs/{runId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/workflows/runs/{runId}/cancel", TestAuth.Json(""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/workflows/runs/{runId}/retry", TestAuth.Json(""), Ct)).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>("/workflows/runs", Ct)).GetArrayLength());
        Assert.Equal(1, (await ana.GetFromJsonAsync<JsonElement>("/workflows/runs", Ct)).GetArrayLength());

        // runId es clave de idempotencia: no sirve para leer la ejecución de otro.
        var reuse = await bob.PostAsync($"/workflows/approval/start?runId={runId}", TestAuth.Json("""{"id":"B-1","amount":1}"""), Ct);
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.DoesNotContain("user:ana", await reuse.Content.ReadAsStringAsync(Ct));

        var engine = app.Services.GetRequiredService<WorkflowEngine>();
        Assert.Equal(WorkflowStatus.WaitingForEvent, (await engine.GetRunAsync(runId, Ct))!.Status);
    }

    [Fact]
    public async Task ApprovalsCannotBeForged()
    {
        // Por defecto los eventos por HTTP están deshabilitados.
        await using (var closed = await StartAsync())
        {
            var ana = TestAuth.Client(closed, "ana");
            var runId = await StartRunAsync(ana, "A-1");
            Assert.Equal(HttpStatusCode.Forbidden, (await ana.PostAsync("/workflows/events/order.approval?key=A-1", TestAuth.Json("""{"approved":true}"""), Ct)).StatusCode);
            Assert.Equal(WorkflowStatus.WaitingForEvent, (await closed.Services.GetRequiredService<WorkflowEngine>().GetRunAsync(runId, Ct))!.Status);
        }

        // Regla de la aplicación: solo aprobadores, y nunca sobre su propia ejecución.
        await using var app = await StartAsync(new WorkflowEndpointOptions
        {
            CanSendEvent = async (http, e) =>
            {
                if (!http.User.IsInRole("approver") || e.WaitingRuns.Count == 0)
                {
                    return false;
                }

                var engine = http.RequestServices.GetRequiredService<WorkflowEngine>();
                foreach (var run in e.WaitingRuns)
                {
                    if (await WorkflowEndpoints.GetRunOwnerAsync(engine, run, http.RequestAborted) == EndpointSecurity.GetUserId(http))
                    {
                        return false;
                    }
                }

                return true;
            },
        });
        var requester = TestAuth.Client(app, "ana", "approver");
        var manager = TestAuth.Client(app, "gerente", "approver");
        var outsider = TestAuth.Client(app, "bob");
        var id = await StartRunAsync(requester, "A-2");

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync("/workflows/events/order.approval?key=A-2", TestAuth.Json("""{"approved":true}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PostAsync("/workflows/events/order.approval?key=A-2", TestAuth.Json("""{"approved":true}"""), Ct)).StatusCode);
        var engine = app.Services.GetRequiredService<WorkflowEngine>();
        Assert.Equal(WorkflowStatus.WaitingForEvent, (await engine.GetRunAsync(id, Ct))!.Status);

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync("/workflows/events/order.approval?key=A-2", TestAuth.Json("""{"approved":true}"""), Ct)).StatusCode);
        await engine.WhenIdleAsync();
        Assert.Equal("aprobado", (await engine.GetRunAsync(id, Ct))!.GetOutput<string>());
    }

    [Fact]
    public async Task AnEventOnlyResumesTheRunsThatCanSendEventSaw()
    {
        string? late = null;
        await using var app = await StartAsync(new WorkflowEndpointOptions
        {
            CanSendEvent = async (http, e) =>
            {
                // Entre la autorización y el envío otra ejecución empieza a esperar el mismo evento con la misma clave.
                var engine = http.RequestServices.GetRequiredService<WorkflowEngine>();
                late = (await engine.StartAsync("approval", new Order("A-3", 1), triggeredBy: "user:carla", cancellationToken: http.RequestAborted)).Id;
                return e.WaitingRuns.Count == 1;
            },
        });
        var id = await StartRunAsync(TestAuth.Client(app, "ana"), "A-3");
        var engine = app.Services.GetRequiredService<WorkflowEngine>();

        var response = await TestAuth.Client(app, "gerente").PostAsync("/workflows/events/order.approval?key=A-3", TestAuth.Json("""{"approved":true}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await engine.WhenIdleAsync();

        Assert.Equal("aprobado", (await engine.GetRunAsync(id, Ct))!.GetOutput<string>());
        Assert.Equal(WorkflowStatus.WaitingForEvent, (await engine.GetRunAsync(late!, Ct))!.Status);
        Assert.Equal([id], (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("runs").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task OversizedBodiesAreRejectedBeforeBeingBuffered()
    {
        await using var app = await StartAsync();
        var anonymous = TestAuth.Client(app);
        var big = new string('a', WorkflowEndpoints.MaxRequestBodyBytes + 1);

        // Webhook anónimo: 413 sin llegar al motor (ni siquiera a buscar la automatización).
        var hook = await anonymous.PostAsync("/workflows/hooks/no-existe", new StringContent(big, System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hook.StatusCode);

        // Endpoints autenticados con JSON: también 413.
        var ana = TestAuth.Client(app, "ana");
        var start = await ana.PostAsync("/workflows/approval/start", TestAuth.Json("\"" + big + "\""), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, start.StatusCode);
    }

    [Fact]
    public async Task AutomationOwnerIsResolvedOnTheServer()
    {
        await using var app = await StartAsync(new WorkflowEndpointOptions { CanStart = (_, workflow) => ValueTask.FromResult(workflow == "approval") });
        var ana = TestAuth.Client(app, "ana");
        var bob = TestAuth.Client(app, "bob");
        var engine = app.Services.GetRequiredService<WorkflowEngine>();
        const string Body = """{"id":"mia","name":"Revisión","trigger":{"kind":"interval","every":"01:00:00"},"workflow":"approval","input":{"id":"X","amount":1},"owner":"victima","lastRunId":"run_falso"}""";

        Assert.Equal(HttpStatusCode.OK, (await ana.PostAsync("/workflows/automations", TestAuth.Json(Body), Ct)).StatusCode);
        var saved = (await engine.Store.GetAutomationAsync("mia", Ct))!;
        Assert.Equal("ana", saved.Owner);
        Assert.Null(saved.LastRunId);

        // Otro usuario no la ve, no la sobrescribe, no la ejecuta ni la borra.
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>("/workflows/automations", Ct)).GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsync("/workflows/automations", TestAuth.Json(Body), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync("/workflows/automations/mia/run", TestAuth.Json(""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync("/workflows/automations/mia", Ct)).StatusCode);
        Assert.Equal("ana", (await engine.Store.GetAutomationAsync("mia", Ct))!.Owner);

        // Las ejecuciones que dispara la automatización también son de su dueño.
        var run = await ana.PostAsync("/workflows/automations/mia/run", TestAuth.Json(""), Ct);
        var runId = (await run.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync($"/workflows/runs/{runId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/workflows/runs/{runId}", Ct)).StatusCode);

        // Webhooks sin secreto y workflows no permitidos se rechazan; los secretos no se devuelven.
        Assert.Equal(HttpStatusCode.BadRequest, (await ana.PostAsync("/workflows/automations", TestAuth.Json("""{"id":"abierto","name":"h","trigger":{"kind":"webhook"},"workflow":"approval"}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.PostAsync("/workflows/automations", TestAuth.Json("""{"id":"x","name":"x","trigger":{"kind":"interval","every":"01:00:00"},"workflow":"internal"}"""), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ana.PostAsync("/workflows/internal/start", TestAuth.Json("{}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ana.PostAsync("/workflows/automations", TestAuth.Json("""{"id":"hook","name":"h","trigger":{"kind":"webhook","secret":"s3cr3t-largo-de-prueba"},"workflow":"approval"}"""), Ct)).StatusCode);
        var listed = await ana.GetStringAsync("/workflows/automations", Ct);
        Assert.DoesNotContain("s3cr3t-largo-de-prueba", listed);
        await engine.WhenIdleAsync();
    }
}

public class SkillEndpointSecurityTests
{
    private static async Task<WebApplication> StartAsync(SkillEndpointOptions? options = null)
    {
        var builder = TestAuth.CreateBuilder();
        var app = builder.Build();
        var skill = Skill.Parse(
            """
            ---
            name: interna
            description: Procedimiento interno de la empresa.
            ---

            # Interna
            """,
            SkillFiles.FromText(new Dictionary<string, string> { ["references/precios.md"] = "margen 40%" }));
        app.MapSkills(library: new SkillLibrary([skill]), options: options);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task SkillFilesRequireAuthenticationByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync();
        var anonymous = TestAuth.Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/skills", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/skills/interna/files/references/precios.md", ct)).StatusCode);

        var file = await TestAuth.Client(app, "ana").GetAsync("/skills/interna/files/references/precios.md", ct);
        Assert.Equal("margen 40%", await file.Content.ReadAsStringAsync(ct));
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());

        await using var open = await StartAsync(new SkillEndpointOptions { AllowAnonymous = true });
        Assert.Equal(HttpStatusCode.OK, (await TestAuth.Client(open).GetAsync("/skills", ct)).StatusCode);
    }
}
