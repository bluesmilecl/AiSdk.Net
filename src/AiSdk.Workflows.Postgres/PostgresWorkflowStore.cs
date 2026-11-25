using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSdk.Workflows.Postgres.Resources;
using Npgsql;
using NpgsqlTypes;

namespace AiSdk.Workflows.Postgres;

/// <summary>Options for <see cref="PostgresWorkflowStore"/>.</summary>
public sealed class PostgresWorkflowStoreOptions
{
    /// <summary>Schema of the tables (created if it does not exist). Defaults to <c>public</c>.</summary>
    public string Schema { get; init; } = "public";

    /// <summary>
    /// Prefix of tables and indexes: the tables are <c>{prefix}runs</c> and <c>{prefix}automations</c>. Defaults to
    /// <c>aisdk_workflow_</c>. Allows several independent stores in the same schema.
    /// </summary>
    public string TablePrefix { get; init; } = "aisdk_workflow_";

    /// <summary>
    /// Creates the schema, tables and indexes on first use if any are missing (true by default). If the application
    /// connects with a user without DDL permissions, disable it and create the schema at deployment time with
    /// <see cref="PostgresWorkflowStore.EnsureSchemaAsync"/> and a user that does have them.
    /// </summary>
    public bool CreateIfNotExists { get; init; } = true;
}

/// <summary>
/// PostgreSQL workflow store: several engine replicas, on the same machine or on others, share runs and automations
/// through the database.
/// </summary>
/// <remarks>
/// <para>
/// Each run and automation is saved in full as JSON (<see cref="JsonSchema.DefaultSerializerOptions"/>), along with
/// columns holding what the engine's queries filter and sort by (status, <c>resume_at</c>, <c>lease_expires_at</c>, awaited
/// event...) and partial indexes on the active runs, so the cycles do not scan the history. The criteria are those of
/// <see cref="WorkflowRunQueries"/>.
/// </para>
/// <para>
/// <b>Optimistic concurrency</b>: <c>UPDATE ... WHERE id = $1 AND version = $n</c> for existing rows and
/// <c>INSERT ... ON CONFLICT (id) DO NOTHING</c> for new ones; 0 affected rows = <see cref="WorkflowConcurrencyException"/>.
/// </para>
/// <para>
/// The JSON is stored in a <c>json</c> column (the text as is), not <c>jsonb</c>: <c>jsonb</c> reorders properties and
/// System.Text.Json requires the discriminator of a polymorphic type (<c>kind</c> of <see cref="AutomationTrigger"/>,
/// <c>type</c> of the parts of a message saved in a step) to be the first one; in addition, <c>jsonb</c> rejects <c>\u0000</c>.
/// Date columns have PostgreSQL's precision (microseconds); the JSON keeps the exact value.
/// </para>
/// </remarks>
public sealed partial class PostgresWorkflowStore : IWorkflowStore, IAsyncDisposable, IDisposable
{
    // Estados como los escribe el motor (nombres del enum, igual que en el JSON).
    private const string Suspended = $"('{nameof(WorkflowStatus.Sleeping)}', '{nameof(WorkflowStatus.WaitingForEvent)}')";
    private const string Unclaimed = $"('{nameof(WorkflowStatus.Pending)}', '{nameof(WorkflowStatus.Running)}')";
    private const string Finished = $"('{nameof(WorkflowStatus.Completed)}', '{nameof(WorkflowStatus.Failed)}', '{nameof(WorkflowStatus.Cancelled)}')";
    private const string Waiting = $"'{nameof(WorkflowStatus.WaitingForEvent)}'";

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly PostgresWorkflowStoreOptions _options;
    private readonly string _runs;
    private readonly string _automations;
    private readonly string[] _relations;
    private readonly string _ddl;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <param name="dataSource">Npgsql data source (shared by all replicas that point to the same database).</param>
    /// <param name="options">Schema, table prefix and automatic creation.</param>
    public PostgresWorkflowStore(NpgsqlDataSource dataSource, PostgresWorkflowStoreOptions? options = null)
        : this(dataSource, options ?? new PostgresWorkflowStoreOptions(), ownsDataSource: false)
    {
    }

    private PostgresWorkflowStore(NpgsqlDataSource dataSource, PostgresWorkflowStoreOptions options, bool ownsDataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var names = Names.Resolve(options);
        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _options = options;

        var schema = Quote(options.Schema);
        string Index(string name) => Quote(names.Index(name));
        _runs = $"{schema}.{Quote(names.Runs)}";
        _automations = $"{schema}.{Quote(names.Automations)}";
        _relations = [names.Runs, names.Automations, .. Names.Indexes.Select(names.Index)];
        _ddl = $"""
            CREATE TABLE IF NOT EXISTS {_runs} (
                id text PRIMARY KEY,
                workflow_name text NOT NULL,
                status text NOT NULL,
                version bigint NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                finished_at timestamptz,
                resume_at timestamptz,
                lease_expires_at timestamptz,
                wait_event text,
                wait_key text,
                data json NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {Index("runs_created_idx")} ON {_runs} (created_at);
            CREATE INDEX IF NOT EXISTS {Index("runs_status_idx")} ON {_runs} (status, created_at);
            CREATE INDEX IF NOT EXISTS {Index("runs_workflow_idx")} ON {_runs} (workflow_name, created_at);
            CREATE INDEX IF NOT EXISTS {Index("runs_resume_idx")} ON {_runs} (resume_at) WHERE status IN {Suspended};
            CREATE INDEX IF NOT EXISTS {Index("runs_lease_idx")} ON {_runs} (lease_expires_at) WHERE status IN {Unclaimed};
            CREATE INDEX IF NOT EXISTS {Index("runs_waiting_idx")} ON {_runs} (wait_event, wait_key) WHERE status = {Waiting};
            CREATE INDEX IF NOT EXISTS {Index("runs_finished_idx")} ON {_runs} (finished_at) WHERE status IN {Finished};
            CREATE TABLE IF NOT EXISTS {_automations} (
                id text PRIMARY KEY,
                enabled boolean NOT NULL,
                next_run_at timestamptz,
                created_at timestamptz NOT NULL,
                version bigint NOT NULL,
                data json NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {Index("automations_due_idx")} ON {_automations} (next_run_at) WHERE enabled;
            """;

        // WorkflowRunQueries.IsDue: cada rama del OR coincide con un índice parcial (resume_idx, lease_idx). El orden es DueAt.
        DueRunsSql = $"""
            SELECT version, data FROM {_runs}
            WHERE (status IN {Suspended} AND resume_at <= $1)
               OR (status IN {Unclaimed} AND lease_expires_at <= $1)
               OR (status IN {Unclaimed} AND lease_expires_at IS NULL)
            ORDER BY CASE WHEN status IN {Suspended} THEN COALESCE(resume_at, updated_at) ELSE COALESCE(lease_expires_at, updated_at) END, id
            LIMIT $2
            """;

        // WorkflowRunQueries.IsWaitingFor: sin clave o con la clave pedida (con $2 nulo, solo las que esperan sin clave).
        WaitingRunsSql = $"""
            SELECT version, data FROM {_runs}
            WHERE status = {Waiting} AND wait_event = $1 AND (wait_key IS NULL OR wait_key = $2)
            ORDER BY created_at, id
            LIMIT $3
            """;

        // Como mucho $2 filas, las más antiguas. ANY(ARRAY(...)) borra por clave primaria (con IN el planificador recorre la
        // tabla para el semi-join). SKIP LOCKED: dos réplicas que purgan a la vez no se esperan; FOR UPDATE vuelve a
        // comprobar la fila si cambió entretanto (p. ej. RetryAsync la volvió a dejar pendiente).
        DeleteFinishedSql = $"""
            DELETE FROM {_runs} WHERE id = ANY(ARRAY(
                SELECT id FROM {_runs}
                WHERE status IN {Finished} AND finished_at < $1
                ORDER BY finished_at
                LIMIT $2
                FOR UPDATE SKIP LOCKED))
            """;

        DueAutomationsSql = $"""
            SELECT version, data FROM {_automations}
            WHERE enabled AND next_run_at <= $1
            ORDER BY next_run_at, id
            LIMIT $2
            """;
    }

    /// <summary>
    /// Creates the store from a connection string. The store owns the data source: it is disposed of with
    /// <see cref="DisposeAsync"/> (the DI container does this when the application shuts down).
    /// </summary>
    public static PostgresWorkflowStore Create(string connectionString, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        options ??= new PostgresWorkflowStoreOptions();
        Names.Resolve(options); // Se valida antes de crear la fuente de datos.
        return new PostgresWorkflowStore(NpgsqlDataSource.Create(connectionString), options, ownsDataSource: true);
    }

    internal string DueRunsSql { get; }

    internal string WaitingRunsSql { get; }

    internal string DeleteFinishedSql { get; }

    internal string DueAutomationsSql { get; }

    /// <summary>
    /// Creates any missing schema, tables and indexes. It is idempotent and safe with several replicas at once (it is
    /// serialized with a PostgreSQL advisory lock); if everything exists, it runs no DDL and does not lock the tables. Works
    /// even if <see cref="PostgresWorkflowStoreOptions.CreateIfNotExists"/> is false (to create it at deployment time).
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CreateMissingAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>With <see cref="PostgresWorkflowStoreOptions.CreateIfNotExists"/>, creates whatever is missing on first use.</summary>
    private async ValueTask EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (_initialized || !_options.CreateIfNotExists)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await CreateMissingAsync(cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task CreateMissingAsync(CancellationToken cancellationToken)
    {
        // CREATE ... IF NOT EXISTS exige permisos de DDL y CREATE INDEX bloquea las escrituras de la tabla aunque el índice
        // exista: si todo existe (lo normal al arrancar cada réplica) no se ejecuta.
        if (await CountExistingAsync(null, cancellationToken).ConfigureAwait(false) == _relations.Length)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Dos réplicas que crean el mismo esquema o las mismas tablas a la vez pueden chocar en el catálogo: se turnan con un
        // bloqueo consultivo por esquema (también los almacenes con otro prefijo, que pueden crear el mismo esquema).
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            lockCommand.Parameters.Add(Text($"AiSdk.Workflows.Postgres:{_options.Schema}"));
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // CREATE SCHEMA IF NOT EXISTS exige permiso de creación en la base aunque el esquema exista (p. ej. public).
        bool schemaExists;
        await using (var schemaCommand = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = $1)", connection, transaction))
        {
            schemaCommand.Parameters.Add(Text(_options.Schema));
            schemaExists = (bool)(await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!schemaExists || await CountExistingAsync(connection, cancellationToken).ConfigureAwait(false) < _relations.Length)
        {
            var ddl = schemaExists ? _ddl : $"CREATE SCHEMA IF NOT EXISTS {Quote(_options.Schema)};\n{_ddl}";
            await using var command = new NpgsqlCommand(ddl, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many of the store's tables and indexes already exist.</summary>
    private async Task<long> CountExistingAsync(NpgsqlConnection? connection, CancellationToken cancellationToken)
    {
        const string Sql = "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = $1 AND c.relname = ANY($2)";
        await using var command = connection is null ? _dataSource.CreateCommand(Sql) : new NpgsqlCommand(Sql, connection);
        command.Parameters.Add(Text(_options.Schema));
        command.Parameters.Add(new NpgsqlParameter { Value = _relations, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    // ---------------------------------------------------------------------
    // Ejecuciones
    // ---------------------------------------------------------------------

    public async Task SaveRunAsync(WorkflowRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var expected = run.Version;
        run.Version = expected + 1;
        try
        {
            var json = JsonSerializer.Serialize(run, JsonSchema.DefaultSerializerOptions);
            await using var command = _dataSource.CreateCommand(expected == 0
                ? $"""
                    INSERT INTO {_runs} (id, workflow_name, status, version, created_at, updated_at, finished_at, resume_at, lease_expires_at, wait_event, wait_key, data)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
                    ON CONFLICT (id) DO NOTHING
                    """
                : $"""
                    UPDATE {_runs} SET workflow_name = $2, status = $3, version = $4, created_at = $5, updated_at = $6, finished_at = $7,
                        resume_at = $8, lease_expires_at = $9, wait_event = $10, wait_key = $11, data = $12
                    WHERE id = $1 AND version = $13
                    """);
            command.Parameters.Add(Text(run.Id));
            command.Parameters.Add(Text(run.WorkflowName));
            command.Parameters.Add(Text(run.Status.ToString()));
            command.Parameters.Add(Bigint(run.Version));
            command.Parameters.Add(Time(run.CreatedAt));
            command.Parameters.Add(Time(run.UpdatedAt));
            command.Parameters.Add(Time(run.IsFinished ? WorkflowRunQueries.FinishedAt(run) : null));
            command.Parameters.Add(Time(run.ResumeAt));
            command.Parameters.Add(Time(run.LeaseExpiresAt));
            command.Parameters.Add(Text(run.Waiting?.EventName));
            command.Parameters.Add(Text(run.Waiting?.Key));
            command.Parameters.Add(Json(json));
            if (expected != 0)
            {
                command.Parameters.Add(Bigint(expected));
            }

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw await ConflictAsync(Strings.Store_RunChanged, _runs, run.Id, expected, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            run.Version = expected;
            throw;
        }
    }

    public async Task<WorkflowRun?> GetRunAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var runs = await QueryAsync($"SELECT version, data FROM {_runs} WHERE id = $1", ReadRun, cancellationToken, Text(id)).ConfigureAwait(false);
        return runs.Count == 0 ? null : runs[0];
    }

    public Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(WorkflowStatus? status = null, string? workflowName = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var parameters = new List<NpgsqlParameter>();
        var conditions = new List<string>();
        if (status is { } s)
        {
            parameters.Add(Text(s.ToString()));
            conditions.Add($"status = ${parameters.Count}");
        }

        if (workflowName is not null)
        {
            parameters.Add(Text(workflowName));
            conditions.Add($"workflow_name = ${parameters.Count}");
        }

        parameters.Add(Integer(limit));
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        return LimitedAsync(limit, $"SELECT version, data FROM {_runs}{where} ORDER BY created_at DESC, id DESC LIMIT ${parameters.Count}", ReadRun, cancellationToken, [.. parameters]);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListDueRunsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) =>
        LimitedAsync(limit, DueRunsSql, ReadRun, cancellationToken, Time(now), Integer(limit));

    public Task<IReadOnlyList<WorkflowRun>> ListRunsWaitingForEventAsync(string eventName, string? key, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        return LimitedAsync(limit, WaitingRunsSql, ReadRun, cancellationToken, Text(eventName), Text(key), Integer(limit));
    }

    public async Task<int> DeleteFinishedRunsAsync(DateTimeOffset finishedBefore, int limit = 1000, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return 0;
        }

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _dataSource.CreateCommand(DeleteFinishedSql);
        command.Parameters.Add(Time(finishedBefore));
        command.Parameters.Add(Integer(limit));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static WorkflowRun ReadRun(NpgsqlDataReader reader)
    {
        var run = JsonSerializer.Deserialize<WorkflowRun>(reader.GetString(1), JsonSchema.DefaultSerializerOptions)!;
        run.Version = reader.GetInt64(0); // La columna manda.
        return run;
    }

    // ---------------------------------------------------------------------
    // Automatizaciones
    // ---------------------------------------------------------------------

    public async Task<Automation> SaveAutomationAsync(Automation automation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(automation);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var expected = automation.Version;
        var json = JsonSerializer.Serialize(automation with { Version = expected + 1 }, JsonSchema.DefaultSerializerOptions);
        await using var command = _dataSource.CreateCommand(expected == 0
            ? $"""
                INSERT INTO {_automations} (id, enabled, next_run_at, created_at, version, data)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT (id) DO NOTHING
                """
            : $"""
                UPDATE {_automations} SET enabled = $2, next_run_at = $3, created_at = $4, version = $5, data = $6
                WHERE id = $1 AND version = $7
                """);
        command.Parameters.Add(Text(automation.Id));
        command.Parameters.Add(new NpgsqlParameter { Value = automation.Enabled, NpgsqlDbType = NpgsqlDbType.Boolean });
        command.Parameters.Add(Time(automation.NextRunAt));
        command.Parameters.Add(Time(automation.CreatedAt));
        command.Parameters.Add(Bigint(expected + 1));
        command.Parameters.Add(Json(json));
        if (expected != 0)
        {
            command.Parameters.Add(Bigint(expected));
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw await ConflictAsync(Strings.Store_AutomationChanged, _automations, automation.Id, expected, cancellationToken).ConfigureAwait(false);
        }

        // Copia de lo guardado: quien la recibe no comparte referencias con el original.
        return JsonSerializer.Deserialize<Automation>(json, JsonSchema.DefaultSerializerOptions)!;
    }

    public async Task<Automation?> GetAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var automations = await QueryAsync($"SELECT version, data FROM {_automations} WHERE id = $1", ReadAutomation, cancellationToken, Text(id)).ConfigureAwait(false);
        return automations.Count == 0 ? null : automations[0];
    }

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT version, data FROM {_automations} ORDER BY created_at, id", ReadAutomation, cancellationToken);

    public Task<IReadOnlyList<Automation>> ListDueAutomationsAsync(DateTimeOffset now, int limit = 100, CancellationToken cancellationToken = default) =>
        LimitedAsync(limit, DueAutomationsSql, ReadAutomation, cancellationToken, Time(now), Integer(limit));

    public async Task DeleteAutomationAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _dataSource.CreateCommand($"DELETE FROM {_automations} WHERE id = $1");
        command.Parameters.Add(Text(id));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Automation ReadAutomation(NpgsqlDataReader reader) =>
        JsonSerializer.Deserialize<Automation>(reader.GetString(1), JsonSchema.DefaultSerializerOptions)! with { Version = reader.GetInt64(0) };

    // ---------------------------------------------------------------------
    // Utilidades
    // ---------------------------------------------------------------------

    /// <summary>As with the other stores, a limit of 0 or less returns an empty list.</summary>
    private async Task<IReadOnlyList<T>> LimitedAsync<T>(int limit, string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken, params NpgsqlParameter[] parameters) =>
        limit <= 0 ? [] : await QueryAsync(sql, read, cancellationToken, parameters).ConfigureAwait(false);

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(read(reader));
        }

        return results;
    }

    /// <summary>Concurrency error with the saved version (0 if it no longer exists), with the same text as the other stores.</summary>
    private async Task<WorkflowConcurrencyException> ConflictAsync(string format, string table, string id, long expected, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand($"SELECT version FROM {table} WHERE id = $1");
        command.Parameters.Add(Text(id));
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long version ? version : 0;
        return new WorkflowConcurrencyException(string.Format(CultureInfo.CurrentCulture, format, id, stored, expected));
    }

    private static NpgsqlParameter Text(string? value) => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Json(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Json };

    private static NpgsqlParameter Bigint(long value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Bigint };

    private static NpgsqlParameter Integer(int value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Integer };

    // Npgsql solo escribe timestamptz desde UTC: un DateTimeOffset con otra zona se convierte (es el mismo instante).
    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { Value = value is { } v ? v.ToUniversalTime() : DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static string Quote(string name) => $"\"{name}\"";

    /// <summary>Disposes of the data source if the store created it (<see cref="Create"/>); one that was passed in is left untouched.</summary>
    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="DisposeAsync"/>
    public void Dispose()
    {
        _initLock.Dispose();
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    /// <summary>Validated names of tables and indexes (only letters, digits and _; at most 63 characters: no SQL injection).</summary>
    private sealed record Names(string Prefix)
    {
        public static readonly string[] Indexes =
        [
            "runs_created_idx", "runs_status_idx", "runs_workflow_idx", "runs_resume_idx", "runs_lease_idx", "runs_waiting_idx",
            "runs_finished_idx", "automations_due_idx",
        ];

        public string Runs => Prefix + "runs";

        public string Automations => Prefix + "automations";

        public string Index(string name) => Prefix + name;

        public static Names Resolve(PostgresWorkflowStoreOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            Validate(options.Schema, nameof(options.Schema));
            ArgumentNullException.ThrowIfNull(options.TablePrefix, nameof(options.TablePrefix));
            var names = new Names(options.TablePrefix);
            foreach (var name in (string[])[names.Runs, names.Automations, .. Indexes.Select(names.Index)])
            {
                Validate(name, nameof(options.TablePrefix));
            }

            return names;
        }

        private static void Validate(string? name, string option)
        {
            if (name is null || !IdentifierRegex().IsMatch(name))
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, Strings.Store_InvalidIdentifier, option, name), option);
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}\z")]
    private static partial Regex IdentifierRegex();
}
