using System.Text.Json;
using AiSdk.Workflows;
using AiSdk.Workflows.Postgres;
using Npgsql;

namespace AiSdk.Tests;

/// <summary>Almacenes de workflows contra los que se ejecuta el contrato de <see cref="IWorkflowStore"/>.</summary>
public enum WorkflowStoreKind
{
    Memory,
    FileSystem,
    Postgres,
}

/// <summary>Datos de las teorías: cada prueba de contrato se ejecuta con los tres almacenes.</summary>
public sealed class AllWorkflowStores : TheoryData<WorkflowStoreKind>
{
    public AllWorkflowStores()
        : base(WorkflowStoreKind.Memory, WorkflowStoreKind.FileSystem, WorkflowStoreKind.Postgres)
    {
    }
}

/// <summary>
/// Un almacén vacío para una prueba y otra instancia sobre los mismos datos (<see cref="Peer"/>: otro proceso u otra
/// réplica). PostgreSQL usa un esquema propio que se borra al terminar y se omite si no está definida <c>AISDK_TEST_PG</c>
/// (p. ej. <c>Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=postgres</c>).
/// </summary>
internal sealed class TestWorkflowStores : IAsyncDisposable
{
    private readonly Func<ValueTask> _cleanup;

    private TestWorkflowStores(IWorkflowStore store, IWorkflowStore peer, Func<ValueTask> cleanup)
    {
        Store = store;
        Peer = peer;
        _cleanup = cleanup;
    }

    public IWorkflowStore Store { get; }

    public IWorkflowStore Peer { get; }

    public static TestWorkflowStores Create(WorkflowStoreKind kind)
    {
        switch (kind)
        {
            case WorkflowStoreKind.Memory:
                var memory = new InMemoryWorkflowStore();
                return new(memory, memory, () => ValueTask.CompletedTask);

            case WorkflowStoreKind.FileSystem:
                var dir = Path.Combine(Path.GetTempPath(), "aisdk-wf-contract-" + Guid.NewGuid().ToString("N"));
                var first = new FileSystemWorkflowStore(dir);
                var second = new FileSystemWorkflowStore(dir); // otro proceso sobre el mismo directorio
                return new(first, second, () =>
                {
                    first.Dispose();
                    second.Dispose();
                    Directory.Delete(dir, true);
                    return ValueTask.CompletedTask;
                });

            default:
                var connection = Environment.GetEnvironmentVariable("AISDK_TEST_PG");
                Assert.SkipWhen(string.IsNullOrEmpty(connection), "AISDK_TEST_PG no configurada");
                var options = new PostgresWorkflowStoreOptions { Schema = "aisdk_test_wf_" + Guid.NewGuid().ToString("N")[..8] };

                // Cada réplica con su propia fuente de datos (su pool de conexiones), como dos máquinas.
                var dataSourceA = NpgsqlDataSource.Create(connection!);
                var dataSourceB = NpgsqlDataSource.Create(connection!);
                var storeA = new PostgresWorkflowStore(dataSourceA, options);
                var storeB = new PostgresWorkflowStore(dataSourceB, options);
                return new(storeA, storeB, async () =>
                {
                    await storeA.DisposeAsync();
                    await storeB.DisposeAsync();
                    await using (var drop = dataSourceA.CreateCommand($"DROP SCHEMA IF EXISTS \"{options.Schema}\" CASCADE"))
                    {
                        await drop.ExecuteNonQueryAsync();
                    }

                    await dataSourceA.DisposeAsync();
                    await dataSourceB.DisposeAsync();
                });
        }
    }

    public ValueTask DisposeAsync() => _cleanup();
}

/// <summary>
/// Contrato de <see cref="IWorkflowStore"/> (concurrencia optimista, consultas indexadas con su filtro, orden y límite,
/// retención y fidelidad del JSON) y motores que comparten un almacén, con los tres almacenes.
/// </summary>
public class WorkflowStoreContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // En el futuro y en segundos enteros: el almacén en disco compara la fecha de modificación de los archivos al purgar
    // y PostgreSQL guarda microsegundos en las columnas.
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());

    private static WorkflowRun Run(string id, WorkflowStatus status, int createdMinute = 0, string workflow = "w", TimeSpan? offset = null) => new()
    {
        Id = id,
        WorkflowName = workflow,
        Status = status,
        Input = JsonSerializer.SerializeToElement(new { id }),
        CreatedAt = T0.AddMinutes(createdMinute).ToOffset(offset ?? TimeSpan.Zero),
        UpdatedAt = T0.AddMinutes(createdMinute),
    };

    private static async Task SaveAllAsync(IWorkflowStore store, IEnumerable<WorkflowRun> runs)
    {
        foreach (var run in runs)
        {
            await store.SaveRunAsync(run, Ct);
        }
    }

    private static Automation NewAutomation(string id, int createdMinute = 0, DateTimeOffset? nextRunAt = null, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Trigger = new IntervalTrigger(TimeSpan.FromHours(1)),
        Workflow = "w",
        CreatedAt = T0.AddMinutes(createdMinute),
        NextRunAt = nextRunAt,
        Enabled = enabled,
    };

    // ------------------------------------------------------------------
    // Concurrencia optimista (la de las ejecuciones está en WorkflowSecurityTests)
    // ------------------------------------------------------------------

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task AutomationsUseOptimisticConcurrencyAcrossInstances(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        var (store, peer) = (stores.Store, stores.Peer);

        var automation = NewAutomation("a1", nextRunAt: T0.AddHours(1));
        var saved = await store.SaveAutomationAsync(automation, Ct);
        Assert.Equal(1, saved.Version);
        Assert.Equal(0, automation.Version);

        // Nueva con un id que ya existe, o con versión de una que no existe: conflicto.
        await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => peer.SaveAutomationAsync(automation, Ct));
        await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => store.SaveAutomationAsync(automation with { Id = "a2", Version = 4 }, Ct));
        Assert.Null(await peer.GetAutomationAsync("a2", Ct));

        var mine = (await store.GetAutomationAsync("a1", Ct))!;
        var theirs = (await peer.GetAutomationAsync("a1", Ct))!;
        Assert.Equal(2, (await store.SaveAutomationAsync(mine with { Enabled = false }, Ct)).Version);
        await Assert.ThrowsAsync<WorkflowConcurrencyException>(() => peer.SaveAutomationAsync(theirs with { LastError = "pisaría" }, Ct));

        var stored = (await peer.GetAutomationAsync("a1", Ct))!;
        Assert.False(stored.Enabled);
        Assert.Null(stored.LastError);
        Assert.Equal(2, stored.Version);
        Assert.Equal(new IntervalTrigger(TimeSpan.FromHours(1)), stored.Trigger);

        // En paralelo desde dos instancias: ningún incremento se pierde.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            var target = i % 2 == 0 ? store : peer;
            while (true)
            {
                var current = (await target.GetAutomationAsync("a1", Ct))!;
                try
                {
                    await target.SaveAutomationAsync(current with { ConsecutiveFailures = current.ConsecutiveFailures + 1 }, Ct);
                    return;
                }
                catch (WorkflowConcurrencyException)
                {
                }
            }
        }, Ct)));
        var final = (await store.GetAutomationAsync("a1", Ct))!;
        Assert.Equal(10, final.ConsecutiveFailures);
        Assert.Equal(12, final.Version);

        await peer.DeleteAutomationAsync("a1", Ct);
        Assert.Null(await store.GetAutomationAsync("a1", Ct));
        Assert.Equal(1, (await store.SaveAutomationAsync(automation, Ct)).Version); // se puede volver a crear
    }

    // ------------------------------------------------------------------
    // Consultas: filtro, orden y límite
    // ------------------------------------------------------------------

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task ListRunsFiltersByStatusAndWorkflowNewestFirst(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        await SaveAllAsync(stores.Store,
        [
            Run("p1", WorkflowStatus.Pending, 1, "a"),
            Run("c1", WorkflowStatus.Completed, 2, "a"),
            Run("p2", WorkflowStatus.Pending, 3, "b"),
            Run("f1", WorkflowStatus.Failed, 4, "b"),
            Run("p3", WorkflowStatus.Pending, 5, "a"),
            Run("c2", WorkflowStatus.Completed, 6, "b"),
            Run("s1", WorkflowStatus.Sleeping, 7, "a", offset: TimeSpan.FromHours(-3)), // otra zona, el mismo instante
        ]);

        async Task<List<string>> Ids(WorkflowStatus? status = null, string? workflow = null, int limit = 100) =>
            (await stores.Peer.ListRunsAsync(status, workflow, limit, Ct)).Select(r => r.Id).ToList();

        Assert.Equal(["s1", "c2", "p3", "f1", "p2", "c1", "p1"], await Ids());
        Assert.Equal(["s1", "c2", "p3"], await Ids(limit: 3));
        Assert.Equal(["p3", "p2", "p1"], await Ids(WorkflowStatus.Pending));
        Assert.Equal(["p3", "p2"], await Ids(WorkflowStatus.Pending, limit: 2));
        Assert.Equal(["c2", "c1"], await Ids(WorkflowStatus.Completed));
        Assert.Equal(["s1", "p3", "c1", "p1"], await Ids(workflow: "a"));
        Assert.Equal(["p3", "p1"], await Ids(WorkflowStatus.Pending, "a"));
        Assert.Equal(["c2"], await Ids(WorkflowStatus.Completed, "b"));
        Assert.Empty(await Ids(WorkflowStatus.Cancelled));
        Assert.Empty(await Ids(limit: 0));
    }

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task DueRunsFollowWorkflowRunQueriesMostOverdueFirst(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        var now = T0.AddHours(1);
        WorkflowRun With(WorkflowRun run, Action<WorkflowRun> set)
        {
            set(run);
            return run;
        }

        WorkflowRun[] runs =
        [
            With(Run("sleep-due", WorkflowStatus.Sleeping, 1), r => r.ResumeAt = now.AddMinutes(-10).ToOffset(TimeSpan.FromHours(5))), // otra zona
            With(Run("sleep-now", WorkflowStatus.Sleeping, 2), r => r.ResumeAt = now),
            With(Run("sleep-later", WorkflowStatus.Sleeping, 3), r => r.ResumeAt = now.AddMinutes(1)),
            Run("sleep-without-date", WorkflowStatus.Sleeping, 4),
            With(Run("wait-timed-out", WorkflowStatus.WaitingForEvent, 5), r =>
            {
                r.ResumeAt = now.AddMinutes(-30);
                r.Waiting = new EventWait("e", null, "event:e", r.ResumeAt);
            }),
            With(Run("wait-forever", WorkflowStatus.WaitingForEvent, 6), r => r.Waiting = new EventWait("e", null, "event:e")),
            With(Run("pending-free", WorkflowStatus.Pending, 7), r => r.UpdatedAt = now.AddMinutes(-20)),
            With(Run("pending-free-touched-later", WorkflowStatus.Pending, 8), r => r.UpdatedAt = now.AddMinutes(5)), // sin reclamo: vencida igual
            With(Run("running-abandoned", WorkflowStatus.Running, 9), r =>
            {
                r.LeaseOwner = "réplica-caída";
                r.LeaseExpiresAt = now.AddMinutes(-40);
            }),
            With(Run("running-leased", WorkflowStatus.Running, 10), r =>
            {
                r.LeaseOwner = "otra";
                r.LeaseExpiresAt = now.AddMinutes(1);
            }),
            With(Run("pending-leased", WorkflowStatus.Pending, 11), r =>
            {
                r.LeaseOwner = "otra";
                r.LeaseExpiresAt = now.AddMinutes(2);
            }),
            With(Run("completed", WorkflowStatus.Completed, 12), r => r.ResumeAt = now.AddHours(-1)),
            With(Run("failed", WorkflowStatus.Failed, 13), r => r.LeaseExpiresAt = now.AddHours(-1)),
        ];
        await SaveAllAsync(stores.Store, runs);

        string[] expected = ["running-abandoned", "wait-timed-out", "pending-free", "sleep-due", "sleep-now", "pending-free-touched-later"];
        Assert.Equal(expected, runs.Where(r => WorkflowRunQueries.IsDue(r, now)).OrderBy(WorkflowRunQueries.DueAt).Select(r => r.Id));

        var due = await stores.Peer.ListDueRunsAsync(now, cancellationToken: Ct);
        Assert.Equal(expected, due.Select(r => r.Id));
        Assert.All(due, r => Assert.Equal(1, r.Version));
        Assert.Equal("e", due[1].Waiting!.EventName);
        Assert.Equal(expected[..2], (await stores.Peer.ListDueRunsAsync(now, 2, Ct)).Select(r => r.Id));
        Assert.Empty(await stores.Peer.ListDueRunsAsync(now, 0, Ct));
        Assert.Equal(["running-abandoned", "wait-timed-out", "pending-free", "pending-free-touched-later"], (await stores.Peer.ListDueRunsAsync(now.AddMinutes(-15), cancellationToken: Ct)).Select(r => r.Id));
    }

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task RunsWaitingForAnEventMatchTheKeyOldestFirst(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        WorkflowRun Waiting(string id, WorkflowStatus status, int createdMinute, string eventName, string? key)
        {
            var run = Run(id, status, createdMinute);
            run.Waiting = new EventWait(eventName, key, $"event:{eventName}:{key}");
            return run;
        }

        await SaveAllAsync(stores.Store,
        [
            Waiting("any-key", WorkflowStatus.WaitingForEvent, 5, "approved", null),
            Waiting("k1", WorkflowStatus.WaitingForEvent, 1, "approved", "k1"),
            Waiting("k2", WorkflowStatus.WaitingForEvent, 2, "approved", "k2"),
            Waiting("k1-later", WorkflowStatus.WaitingForEvent, 3, "approved", "k1"),
            Waiting("other-event", WorkflowStatus.WaitingForEvent, 0, "rejected", "k1"),
            Waiting("sleeping", WorkflowStatus.Sleeping, 0, "approved", "k1"),
            Waiting("completed", WorkflowStatus.Completed, 0, "approved", null),
        ]);

        async Task<List<string>> Ids(string eventName, string? key, int limit = 100) =>
            (await stores.Peer.ListRunsWaitingForEventAsync(eventName, key, limit, Ct)).Select(r => r.Id).ToList();

        Assert.Equal(["k1", "k1-later", "any-key"], await Ids("approved", "k1"));
        Assert.Equal(["k1", "k1-later"], await Ids("approved", "k1", limit: 2));
        Assert.Equal(["k2", "any-key"], await Ids("approved", "k2"));
        Assert.Equal(["any-key"], await Ids("approved", null)); // sin clave: solo las que esperan sin clave
        Assert.Equal(["any-key"], await Ids("approved", "nadie"));
        Assert.Equal(["other-event"], await Ids("rejected", "k1"));
        Assert.Empty(await Ids("missing", null));
        Assert.Empty(await Ids("approved", "k1", limit: 0));
    }

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task DeleteFinishedRunsRespectsTheCutoffAndTheLimit(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        WorkflowRun Finished(string id, WorkflowStatus status, int completedMinute)
        {
            var run = Run(id, status);
            run.CompletedAt = T0.AddMinutes(completedMinute);
            run.UpdatedAt = run.CompletedAt.Value;
            return run;
        }

        var legacy = Run("legacy", WorkflowStatus.Completed);
        legacy.UpdatedAt = T0.AddMinutes(30); // sin CompletedAt: cuenta UpdatedAt
        await SaveAllAsync(stores.Store,
        [
            Finished("c1", WorkflowStatus.Completed, 60),
            Finished("f2", WorkflowStatus.Failed, 120),
            Finished("x3", WorkflowStatus.Cancelled, 180),
            Finished("c4", WorkflowStatus.Completed, 240),
            legacy,
            Run("pending", WorkflowStatus.Pending),
            Run("sleeping", WorkflowStatus.Sleeping),
        ]);

        var cutoff = T0.AddMinutes(210);
        Assert.Equal(3, await stores.Peer.DeleteFinishedRunsAsync(cutoff, 3, Ct));
        Assert.Equal(1, await stores.Peer.DeleteFinishedRunsAsync(cutoff, 10, Ct));
        Assert.Equal(0, await stores.Peer.DeleteFinishedRunsAsync(cutoff, 10, Ct));
        foreach (var id in new[] { "c1", "f2", "x3", "legacy" })
        {
            Assert.Null(await stores.Store.GetRunAsync(id, Ct));
        }

        Assert.Equal(0, await stores.Peer.DeleteFinishedRunsAsync(T0.AddMinutes(240), 10, Ct)); // el corte es exclusivo
        Assert.Equal(1, await stores.Peer.DeleteFinishedRunsAsync(T0.AddMinutes(241), 10, Ct));
        Assert.Equal(["pending", "sleeping"], (await stores.Store.ListRunsAsync(cancellationToken: Ct)).Select(r => r.Id).Order());
    }

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task AutomationsAreListedByCreationAndDueOnesEarliestFirst(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        var now = T0;
        foreach (var automation in new[]
        {
            NewAutomation("late-created", 3, now.AddMinutes(-1)),
            NewAutomation("b", 1, now.AddMinutes(-10)),
            NewAutomation("c", 2, now),
            NewAutomation("future", 4, now.AddMinutes(1)),
            NewAutomation("disabled", 5, now.AddMinutes(-20), enabled: false),
            NewAutomation("webhook", 0),
        })
        {
            await stores.Store.SaveAutomationAsync(automation, Ct);
        }

        Assert.Equal(["webhook", "b", "c", "late-created", "future", "disabled"], (await stores.Peer.ListAutomationsAsync(Ct)).Select(a => a.Id));
        Assert.Equal(["b", "late-created", "c"], (await stores.Peer.ListDueAutomationsAsync(now, cancellationToken: Ct)).Select(a => a.Id));
        Assert.Equal(["b", "late-created"], (await stores.Peer.ListDueAutomationsAsync(now, 2, Ct)).Select(a => a.Id));
        Assert.Equal(["b"], (await stores.Peer.ListDueAutomationsAsync(now.AddMinutes(-5), cancellationToken: Ct)).Select(a => a.Id));
        Assert.Empty(await stores.Peer.ListDueAutomationsAsync(now, 0, Ct));
        Assert.All(await stores.Peer.ListDueAutomationsAsync(now, cancellationToken: Ct), a => Assert.True(WorkflowRunQueries.IsDue(a, now)));
    }

    /// <summary>
    /// Los discriminadores polimórficos (<c>kind</c>, <c>type</c>) deben seguir siendo la primera propiedad, el texto con
    /// <c>\u0000</c> se conserva y las fechas no pierden precisión (en PostgreSQL, por eso el JSON no va en <c>jsonb</c>).
    /// </summary>
    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task StoredJsonRoundTripsExactly(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        var once = new Automation
        {
            Id = "once",
            Name = "Recordatorio",
            Trigger = new OnceTrigger(T0),
            Workflow = "w",
            Input = JsonSerializer.SerializeToElement(new { texto = "ñandú °C" }),
            CreatedAt = T0,
            NextRunAt = T0,
            Metadata = new Dictionary<string, string> { ["dueño"] = "ana" },
        };
        await stores.Store.SaveAutomationAsync(once, Ct);
        foreach (var automation in new[]
        {
            (await stores.Peer.GetAutomationAsync("once", Ct))!,
            Assert.Single(await stores.Peer.ListAutomationsAsync(Ct)),
            Assert.Single(await stores.Peer.ListDueAutomationsAsync(T0, cancellationToken: Ct)),
        })
        {
            Assert.Equal(new OnceTrigger(T0), automation.Trigger);
            Assert.Equal("ñandú °C", automation.Input!.Value.GetProperty("texto").GetString());
            Assert.Equal("ana", automation.Metadata!["dueño"]);
        }

        ContentPart[] parts = [new TextPart("hola\0mundo"), new ReasoningPart("pienso")];
        var run = Run("r1", WorkflowStatus.Pending);
        run.Output = JsonSerializer.SerializeToElement(parts, JsonSchema.DefaultSerializerOptions);
        run.Steps["paso"] = new StepRecord("paso", JsonSerializer.SerializeToElement<AutomationTrigger>(new CronTrigger("0 9 * * *"), JsonSchema.DefaultSerializerOptions), 1, T0.AddTicks(1234567));
        run.UpdatedAt = T0.AddTicks(7);
        await stores.Store.SaveRunAsync(run, Ct);
        foreach (var stored in new[]
        {
            (await stores.Peer.GetRunAsync("r1", Ct))!,
            Assert.Single(await stores.Peer.ListRunsAsync(cancellationToken: Ct)),
            Assert.Single(await stores.Peer.ListDueRunsAsync(T0, cancellationToken: Ct)),
        })
        {
            Assert.Equal(parts, stored.GetOutput<ContentPart[]>());
            Assert.Equal(new CronTrigger("0 9 * * *"), stored.Steps["paso"].Output!.Value.Deserialize<AutomationTrigger>(JsonSchema.DefaultSerializerOptions));
            Assert.Equal(T0.AddTicks(1234567), stored.Steps["paso"].CompletedAt);
            Assert.Equal(T0.AddTicks(7), stored.UpdatedAt);
        }
    }

    // ------------------------------------------------------------------
    // Dos motores (réplicas) sobre el mismo almacén
    // ------------------------------------------------------------------

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task AnEventSentThroughAnotherReplicaResumesTheRun(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        var after = 0;
        IWorkflow Approval() => Workflow.Create<string, string>("approval", async (ctx, id) =>
        {
            var approval = await ctx.WaitForEventAsync<JsonElement?>("approved", key: id);
            await ctx.StepAsync("after", _ => Task.FromResult(Interlocked.Increment(ref after)));
            return approval?.GetProperty("by").GetString() ?? "vencido";
        });
        var a = new WorkflowEngine(stores.Store).Register(Approval());
        var b = new WorkflowEngine(stores.Peer).Register(Approval());

        var run = await a.StartAsync("approval", "P-1", cancellationToken: Ct);
        var other = await a.StartAsync("approval", "P-2", cancellationToken: Ct);
        Assert.Equal(WorkflowStatus.WaitingForEvent, run.Status);

        // El evento llega a la otra réplica (p. ej. la petición HTTP cayó en otra máquina).
        Assert.Equal([run.Id], await b.SendEventAsync("approved", new { by = "ana" }, key: "P-1", cancellationToken: Ct));
        await b.WhenIdleAsync();
        var done = (await a.GetRunAsync(run.Id, Ct))!;
        Assert.Equal(WorkflowStatus.Completed, done.Status);
        Assert.Equal("ana", done.GetOutput<string>());

        // Nadie la vuelve a ejecutar: ni los ciclos de ambas réplicas ni un evento repetido.
        await Task.WhenAll(a.TickAsync(cancellationToken: Ct), b.TickAsync(cancellationToken: Ct));
        Assert.Empty(await a.SendEventAsync("approved", new { by = "luis" }, key: "P-1", cancellationToken: Ct));
        await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());
        Assert.Equal(1, after);
        Assert.Equal("ana", (await b.GetRunAsync(run.Id, Ct))!.GetOutput<string>());
        Assert.Equal([other.Id], (await stores.Peer.ListRunsWaitingForEventAsync("approved", "P-2", cancellationToken: Ct)).Select(r => r.Id));
    }

    [Theory]
    [ClassData(typeof(AllWorkflowStores))]
    public async Task ADueSleepingRunIsExecutedExactlyOnceByCompetingReplicas(WorkflowStoreKind kind)
    {
        await using var stores = TestWorkflowStores.Create(kind);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var before = 0;
        var after = 0;
        IWorkflow Nap() => Workflow.Create<int, string>("nap", async (ctx, _) =>
        {
            await ctx.StepAsync("before", _ => Task.FromResult(Interlocked.Increment(ref before)));
            await ctx.SleepAsync("nap", TimeSpan.FromHours(1));
            await ctx.StepAsync("after", _ => Task.FromResult(Interlocked.Increment(ref after)));
            return "despierta";
        });

        // Cada réplica con sus opciones: InstanceId distinto.
        var a = new WorkflowEngine(stores.Store, options: new WorkflowEngineOptions { TimeProvider = clock }).Register(Nap());
        var b = new WorkflowEngine(stores.Peer, options: new WorkflowEngineOptions { TimeProvider = clock }).Register(Nap());

        var run = await a.StartAsync("nap", 0, cancellationToken: Ct);
        Assert.Equal(WorkflowStatus.Sleeping, run.Status);
        await Task.WhenAll(a.TickAsync(cancellationToken: Ct), b.TickAsync(cancellationToken: Ct));
        await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());
        Assert.Equal(0, after); // aún no vence

        clock.Now += TimeSpan.FromHours(2);
        await Task.WhenAll(Enumerable.Range(0, 4).SelectMany(_ => new[] { a.TickAsync(cancellationToken: Ct), b.TickAsync(cancellationToken: Ct), a.RecoverAsync(Ct), b.RecoverAsync(Ct) }));
        await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());
        await Task.WhenAll(a.TickAsync(cancellationToken: Ct), b.TickAsync(cancellationToken: Ct));
        await Task.WhenAll(a.WhenIdleAsync(), b.WhenIdleAsync());

        var done = (await stores.Peer.GetRunAsync(run.Id, Ct))!;
        Assert.Equal(WorkflowStatus.Completed, done.Status);
        Assert.Equal("despierta", done.GetOutput<string>());
        Assert.Equal(1, before);
        Assert.Equal(1, after);
        Assert.Equal(2, done.Executions); // un solo reclamo al despertar
        Assert.Null(done.LeaseOwner);
    }
}
