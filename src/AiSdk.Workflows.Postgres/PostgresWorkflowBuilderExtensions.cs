using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AiSdk.Workflows.Postgres;

/// <summary>Registro de <see cref="PostgresWorkflowStore"/> en <c>AddAiSdkWorkflows</c>.</summary>
public static class PostgresWorkflowBuilderExtensions
{
    /// <summary>Persiste ejecuciones y automatizaciones en PostgreSQL con la fuente de datos indicada.</summary>
    public static WorkflowBuilder UsePostgresStore(this WorkflowBuilder builder, NpgsqlDataSource dataSource, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(new PostgresWorkflowStore(dataSource, options));
    }

    /// <summary>
    /// Persiste en PostgreSQL con una cadena de conexión. El almacén crea su propia fuente de datos y el contenedor la
    /// libera al apagar la aplicación.
    /// </summary>
    public static WorkflowBuilder UsePostgresStore(this WorkflowBuilder builder, string connectionString, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return builder.UseStore(_ => PostgresWorkflowStore.Create(connectionString, options));
    }

    /// <summary>
    /// Persiste en PostgreSQL con la <see cref="NpgsqlDataSource"/> registrada en el contenedor (p. ej. con
    /// <c>AddNpgsqlDataSource</c> de Npgsql.DependencyInjection o con Aspire).
    /// </summary>
    public static WorkflowBuilder UsePostgresStore(this WorkflowBuilder builder, PostgresWorkflowStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(services => new PostgresWorkflowStore(services.GetRequiredService<NpgsqlDataSource>(), options));
    }
}
