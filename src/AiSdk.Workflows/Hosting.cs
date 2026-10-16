using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AiSdk.Workflows;

/// <summary>Configuración de <see cref="WorkflowServiceCollectionExtensions.AddAiSdkWorkflows"/>.</summary>
public sealed class WorkflowBuilder
{
    internal List<Func<IServiceProvider, IWorkflow>> Workflows { get; } = [];

    internal List<Automation> Automations { get; } = [];

    internal Func<IServiceProvider, IWorkflowStore>? StoreFactory { get; private set; }

    internal Action<WorkflowEngine, IServiceProvider>? Configure { get; private set; }

    /// <summary>Frecuencia con que se revisan automatizaciones y ejecuciones dormidas.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    public WorkflowEngineOptions Options { get; set; } = new();

    public WorkflowBuilder AddWorkflow(IWorkflow workflow)
    {
        Workflows.Add(_ => workflow);
        return this;
    }

    /// <summary>Registra un workflow construido con servicios del contenedor.</summary>
    public WorkflowBuilder AddWorkflow(Func<IServiceProvider, IWorkflow> factory)
    {
        Workflows.Add(factory);
        return this;
    }

    /// <summary>Automatización definida en código (se crea o actualiza al arrancar).</summary>
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

    /// <summary>Persiste ejecuciones y automatizaciones en disco.</summary>
    public WorkflowBuilder UseFileSystemStore(string directory) => UseStore(new FileSystemWorkflowStore(directory));

    /// <summary>Configuración adicional del motor (p. ej. <c>engine.AddAgentTasks(...)</c>).</summary>
    public WorkflowBuilder ConfigureEngine(Action<WorkflowEngine, IServiceProvider> configure)
    {
        Configure += configure;
        return this;
    }
}

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registra el <see cref="WorkflowEngine"/> como singleton y un servicio en segundo plano que dispara
    /// automatizaciones programadas, reanuda ejecuciones dormidas y recupera las interrumpidas al arrancar.
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

/// <summary>Servicio en segundo plano del motor de workflows.</summary>
public sealed class WorkflowHostedService(WorkflowEngine engine, WorkflowBuilder builder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var automation in builder.Automations)
        {
            var existing = await engine.Store.GetAutomationAsync(automation.Id, stoppingToken).ConfigureAwait(false);
            await engine.AddAutomationAsync(automation with { LastRunAt = existing?.LastRunAt, LastRunId = existing?.LastRunId }, stoppingToken).ConfigureAwait(false);
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
