using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AiSdk.Workflows;

/// <summary>Configuration for <see cref="WorkflowServiceCollectionExtensions.AddAiSdkWorkflows"/>.</summary>
public sealed class WorkflowBuilder
{
    internal List<Func<IServiceProvider, IWorkflow>> Workflows { get; } = [];

    internal List<Automation> Automations { get; } = [];

    internal Func<IServiceProvider, IWorkflowStore>? StoreFactory { get; private set; }

    internal Action<WorkflowEngine, IServiceProvider>? Configure { get; private set; }

    /// <summary>How often automations and sleeping runs are checked.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    public WorkflowEngineOptions Options { get; set; } = new();

    public WorkflowBuilder AddWorkflow(IWorkflow workflow)
    {
        Workflows.Add(_ => workflow);
        return this;
    }

    /// <summary>Registers a workflow built with services from the container.</summary>
    public WorkflowBuilder AddWorkflow(Func<IServiceProvider, IWorkflow> factory)
    {
        Workflows.Add(factory);
        return this;
    }

    /// <summary>Automation defined in code (created or updated at startup).</summary>
    public WorkflowBuilder AddAutomation(Automation automation)
    {
        Automations.Add(automation);
        return this;
    }

    public WorkflowBuilder UseStore(IWorkflowStore store)
    {
        StoreFactory = _ => store;
        return this;
    }

    public WorkflowBuilder UseStore(Func<IServiceProvider, IWorkflowStore> factory)
    {
        StoreFactory = factory;
        return this;
    }

    /// <summary>Persists runs and automations to disk.</summary>
    public WorkflowBuilder UseFileSystemStore(string directory) => UseStore(new FileSystemWorkflowStore(directory));

    /// <summary>Additional engine configuration (e.g. <c>engine.AddAgentTasks(...)</c>).</summary>
    public WorkflowBuilder ConfigureEngine(Action<WorkflowEngine, IServiceProvider> configure)
    {
        Configure += configure;
        return this;
    }
}

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="WorkflowEngine"/> as a singleton, plus a background service that fires scheduled
    /// automations, resumes sleeping runs and recovers interrupted ones at startup.
    /// </summary>
    public static IServiceCollection AddAiSdkWorkflows(this IServiceCollection services, Action<WorkflowBuilder> configure)
    {
        var builder = new WorkflowBuilder();
        configure(builder);
        services.TryAddSingleton(sp => builder.StoreFactory?.Invoke(sp) ?? new InMemoryWorkflowStore());
        services.TryAddSingleton(sp =>
        {
            var engine = new WorkflowEngine(sp.GetRequiredService<IWorkflowStore>(), sp, builder.Options);
            foreach (var factory in builder.Workflows)
            {
                engine.Register(factory(sp));
            }

            builder.Configure?.Invoke(engine, sp);
            return engine;
        });
        services.AddSingleton(builder);
        services.AddHostedService<WorkflowHostedService>();
        return services;
    }
}

/// <summary>Background service of the workflow engine.</summary>
public sealed class WorkflowHostedService(WorkflowEngine engine, WorkflowBuilder builder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var automation in builder.Automations)
        {
            // AddAutomationAsync conserva LastRunAt/LastRunId de la guardada y reintenta si otra réplica la guarda a la vez.
            await engine.AddAutomationAsync(automation, stoppingToken).ConfigureAwait(false);
        }

        await engine.RecoverAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(builder.TickInterval);
        do
        {
            try
            {
                await engine.TickAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un error puntual (p. ej. el almacén no disponible) no debe detener el servicio.
                Console.Error.WriteLine($"[AiSdk.Workflows] Error en el ciclo de automatizaciones: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await engine.WhenIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
