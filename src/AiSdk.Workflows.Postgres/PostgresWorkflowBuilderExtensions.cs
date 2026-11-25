using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AiSdk.Workflows.Postgres;

/// <summary>Registration of <see cref="PostgresWorkflowStore"/> in <c>AddAiSdkWorkflows</c>.</summary>
public static class PostgresWorkflowBuilderExtensions
{
    /// <summary>Persists runs and automations in PostgreSQL with the given data source.</summary>
    public static WorkflowBuilder UsePostgresStore(this WorkflowBuilder builder, NpgsqlDataSource dataSource, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(new PostgresWorkflowStore(dataSource, options));
    }

    /// <summary>
    /// Persists to PostgreSQL with a connection string. The store creates its own data source and the container
    /// disposes of it when the application shuts down.
    /// </summary>
    public static WorkflowBuilder UsePostgresStore(this WorkflowBuilder builder, string connectionString, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return builder.UseStore(_ => PostgresWorkflowStore.Create(connectionString, options));
    }

    /// <summary>
    /// Persists to PostgreSQL with the <see cref="NpgsqlDataSource"/> registered in the container (e.g. with
    /// <c>AddNpgsqlDataSource</c> from Npgsql.DependencyInjection or with Aspire).
    /// </summary>
    public static WorkflowBuilder UsePostgresStore(this WorkflowBuilder builder, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => new PostgresWorkflowStore(services.GetRequiredService<NpgsqlDataSource>(), options));
    }
}
