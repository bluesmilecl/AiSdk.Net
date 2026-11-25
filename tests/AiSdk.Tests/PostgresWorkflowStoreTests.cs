using System.Text.Json;
using AiSdk.Workflows;
using AiSdk.Workflows.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AiSdk.Tests;

/// <summary>
/// Lo propio de <see cref="PostgresWorkflowStore"/> (el contrato común está en <see cref="WorkflowStoreContractTests"/>).
/// Las pruebas con base de datos usan <c>AISDK_TEST_PG</c> y se omiten si no está definida; cada una crea su esquema y lo
/// borra al terminar.
/// </summary>
public class PostgresWorkflowStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Connection()
    {
        var connection = Environment.GetEnvironmentVariable("AISDK_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connection), "AISDK_TEST_PG no configurada");
        return connection!;
    }

    private static string NewSchema() => "aisdk_test_wf_" + Guid.NewGuid().ToString("N")[..8];

    private static async Task DropSchemaAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<List<string>> RelationsAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using var command = dataSource.CreateCommand("SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = $1 AND c.relkind IN ('r', 'i') ORDER BY c.relname COLLATE \"C\"");
        command.Parameters.Add(new NpgsqlParameter { Value = schema });
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static WorkflowRun NewRun(string id) => new() { Id = id, WorkflowName = "w", Input = JsonSerializer.SerializeToElement(new { id }) };

    [Fact]
    public async Task SchemaCreationIsIdempotentConcurrentAndRepairsMissingIndexes()
    {
        var connection = Connection();
        var schema = NewSchema();
        await using var dataSource = NpgsqlDataSource.Create(connection);
        try
        {
            var options = new PostgresWorkflowStoreOptions { Schema = schema, TablePrefix = "wf_" };

            // Varias réplicas arrancan a la vez sobre una base vacía: se turnan y ninguna falla.
            var dataSources = Enumerable.Range(0, 4).Select(_ => NpgsqlDataSource.Create(connection)).ToList();
            try
            {
                await Task.WhenAll(dataSources.Select(ds => new PostgresWorkflowStore(ds, options).EnsureSchemaAsync(Ct)));
            }
            finally
            {
                foreach (var ds in dataSources)
                {
                    await ds.DisposeAsync();
                }
            }

            string[] expected =
            [
                "wf_automations", "wf_automations_due_idx", "wf_automations_pkey", "wf_runs", "wf_runs_created_idx", "wf_runs_finished_idx",
                "wf_runs_lease_idx", "wf_runs_pkey", "wf_runs_resume_idx", "wf_runs_status_idx", "wf_runs_waiting_idx", "wf_runs_workflow_idx",
            ];
            Assert.Equal(expected, await RelationsAsync(dataSource, schema));

            await using var store = new PostgresWorkflowStore(dataSource, options);
            await store.SaveRunAsync(NewRun("r1"), Ct);
            await store.EnsureSchemaAsync(Ct);
            await store.EnsureSchemaAsync(Ct);
            Assert.Equal(1, (await store.GetRunAsync("r1", Ct))!.Version);

            // Falta un índice (p. ej. lo borró alguien): la siguiente réplica que arranca lo vuelve a crear sin tocar los datos.
            await using (var drop = dataSource.CreateCommand($"DROP INDEX \"{schema}\".wf_runs_waiting_idx"))
            {
                await drop.ExecuteNonQueryAsync(Ct);
            }

            await using var restarted = new PostgresWorkflowStore(dataSource, options);
            Assert.Equal(1, (await restarted.GetRunAsync("r1", Ct))!.Version);
            Assert.Equal(expected, await RelationsAsync(dataSource, schema));
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task WithoutCreateIfNotExistsTheSchemaIsOnlyCreatedExplicitly()
    {
        var schema = NewSchema();
        await using var dataSource = NpgsqlDataSource.Create(Connection());
        try
        {
            await using var store = new PostgresWorkflowStore(dataSource, new PostgresWorkflowStoreOptions { Schema = schema, CreateIfNotExists = false });
            var missing = await Assert.ThrowsAsync<PostgresException>(() => store.GetRunAsync("r1", Ct));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, missing.SqlState);
            Assert.Empty(await RelationsAsync(dataSource, schema));

            await store.EnsureSchemaAsync(Ct); // p. ej. en el despliegue, con un usuario con permisos de DDL
            await store.SaveRunAsync(NewRun("r1"), Ct);
            Assert.NotNull(await store.GetRunAsync("r1", Ct));
            Assert.Contains("aisdk_workflow_runs", await RelationsAsync(dataSource, schema));
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task IdentifiersAreValidatedAgainstSqlInjection()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=postgres"); // no se conecta
        foreach (var options in new[]
        {
            new PostgresWorkflowStoreOptions { Schema = "public\"; DROP TABLE users; --" },
            new PostgresWorkflowStoreOptions { Schema = "" },
            new PostgresWorkflowStoreOptions { TablePrefix = "wf-" },
            new PostgresWorkflowStoreOptions { TablePrefix = "1wf_" },
            new PostgresWorkflowStoreOptions { TablePrefix = "wf_\"x" },
            new PostgresWorkflowStoreOptions { Schema = "public\n" }, // en .NET, "$" también acepta un \n final
            new PostgresWorkflowStoreOptions { TablePrefix = "wf_\n" },
            new PostgresWorkflowStoreOptions { TablePrefix = new string('a', 45) }, // "{prefijo}automations_due_idx" > 63
        })
        {
            Assert.Throws<ArgumentException>(() => new PostgresWorkflowStore(dataSource, options));
            Assert.Throws<ArgumentException>(() => PostgresWorkflowStore.Create("Host=127.0.0.1", options));
        }

        await using var empty = new PostgresWorkflowStore(dataSource, new PostgresWorkflowStoreOptions { Schema = "Workflows", TablePrefix = "" });
        await using var longest = new PostgresWorkflowStore(dataSource, new PostgresWorkflowStoreOptions { TablePrefix = new string('a', 44) });
    }

    /// <summary>Las consultas de cada ciclo del motor se resuelven con los índices parciales, sin recorrer la tabla.</summary>
    [Fact]
    public async Task EngineQueriesUseTheIndexes()
    {
        var schema = NewSchema();
        await using var dataSource = NpgsqlDataSource.Create(Connection());
        try
        {
            await using var store = new PostgresWorkflowStore(dataSource, new PostgresWorkflowStoreOptions { Schema = schema });
            await store.EnsureSchemaAsync(Ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var (sql, indexes, parameters) in new (string, string[], object?[])[]
            {
                (store.DueRunsSql, ["aisdk_workflow_runs_resume_idx", "aisdk_workflow_runs_lease_idx"], [now, 100]),
                (store.WaitingRunsSql, ["aisdk_workflow_runs_waiting_idx"], ["approved", "k1", 100]),
                (store.DeleteFinishedSql, ["aisdk_workflow_runs_finished_idx", "aisdk_workflow_runs_pkey"], [now, 1000]),
                (store.DueAutomationsSql, ["aisdk_workflow_automations_due_idx"], [now, 100]),
            })
            {
                await using var batch = dataSource.CreateBatch();
                batch.BatchCommands.Add(new NpgsqlBatchCommand("SET enable_seqscan = off"));
                var explain = new NpgsqlBatchCommand("EXPLAIN " + sql);
                foreach (var value in parameters)
                {
                    explain.Parameters.Add(new NpgsqlParameter { Value = value });
                }

                batch.BatchCommands.Add(explain);
                var plan = new List<string>();
                await using (var reader = await batch.ExecuteReaderAsync(Ct))
                {
                    while (await reader.ReadAsync(Ct))
                    {
                        plan.Add(reader.GetString(0));
                    }
                }

                var text = string.Join('\n', plan);
                Assert.DoesNotContain("Seq Scan", text, StringComparison.Ordinal);
                Assert.All(indexes, index => Assert.Contains(index, text, StringComparison.Ordinal));
            }
        }
        finally
        {
            await DropSchemaAsync(dataSource, schema);
        }
    }

    [Fact]
    public async Task UsePostgresStoreRegistersTheStore()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=postgres"); // no se conecta
        var options = new PostgresWorkflowStoreOptions { Schema = "workflows" };

        var explicitSource = new ServiceCollection().AddAiSdkWorkflows(w => w.UsePostgresStore(dataSource, options)).BuildServiceProvider();
        await using (explicitSource)
        {
            Assert.IsType<PostgresWorkflowStore>(explicitSource.GetRequiredService<IWorkflowStore>());
            Assert.IsType<PostgresWorkflowStore>(explicitSource.GetRequiredService<WorkflowEngine>().Store);
        }

        var fromContainer = new ServiceCollection().AddSingleton(dataSource).AddAiSdkWorkflows(w => w.UsePostgresStore(options)).BuildServiceProvider();
        await using (fromContainer)
        {
            Assert.IsType<PostgresWorkflowStore>(fromContainer.GetRequiredService<IWorkflowStore>());
        }

        // Con una cadena de conexión el almacén es dueño de su fuente de datos y el contenedor lo libera (también sin async).
        using var fromConnectionString = new ServiceCollection().AddAiSdkWorkflows(w => w.UsePostgresStore("Host=127.0.0.1;Database=postgres", options)).BuildServiceProvider();
        Assert.IsType<PostgresWorkflowStore>(fromConnectionString.GetRequiredService<IWorkflowStore>());
    }
}
