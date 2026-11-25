using System.Diagnostics;
using AiSdk.Internal;

namespace AiSdk;

/// <summary>Implementación compartida del bucle de herramientas (generate y stream).</summary>
internal static class GenerationEngine
{
    private static readonly StopCondition DefaultStop = StopCondition.StepCountIs(1);

    internal sealed record StepPlan(
        ILanguageModel Model,
        LanguageModelRequest Request,
        IReadOnlyList<Tool> Tools,
        IReadOnlyList<ModelMessage> SentMessages);

    internal sealed record ToolPhase(IReadOnlyList<ToolCallPart> Calls, List<ToolResultPart> Results, IReadOnlyList<ToolApprovalRequest> PendingApprovals);

    private readonly record struct ProcessedCall(ToolCallPart Call, ToolResultPart? Result, ToolApprovalRequest? PendingApproval = null);

    /// <summary>Resultados de reanudar aprobaciones diferidas: el mensaje de herramienta y las partes a emitir en streaming.</summary>
    internal sealed record ApprovalResolution(ModelMessage Message, IReadOnlyList<StreamPart> Parts);

    public static List<ModelMessage> InitialMessages(GenerateTextOptions o)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxParallelToolCalls, 1, nameof(o.MaxParallelToolCalls));
        var messages = new List<ModelMessage>(o.Messages ?? []);
        if (o.Prompt is not null)
        {
            messages.Add(ModelMessage.User(o.Prompt));
        }

        if (messages.Count == 0)
        {
            throw new ArgumentException("Debe indicarse Prompt o Messages.");
        }

        return messages;
    }

    public static async ValueTask<StepPlan> PrepareAsync(
        GenerateTextOptions o,
        List<ModelMessage> conversation,
        List<StepResult> steps)
    {
        var prepared = o.PrepareStep is null
            ? null
            : await o.PrepareStep(new PrepareStepContext(steps.Count, steps, o.Model, conversation)).ConfigureAwait(false);

        var model = prepared?.Model ?? o.Model;
        var system = prepared?.System ?? o.System;
        var tools = ActiveTools(o, prepared?.ActiveTools ?? o.ActiveTools);

        var sent = new List<ModelMessage>();
        if (!string.IsNullOrEmpty(system))
        {
            sent.Add(ModelMessage.System(system));
        }

        sent.AddRange(prepared?.Messages ?? conversation);

        var format = o.Output?.ResponseFormat;
        var request = new LanguageModelRequest
        {
            Messages = sent,
            Tools = tools.Count > 0 ? tools.Select(t => t.ToDefinition()).ToList() : null,
            ToolChoice = tools.Count > 0 ? prepared?.ToolChoice ?? o.ToolChoice : null,
            ResponseFormat = format is { Type: ResponseFormatType.Json } ? format : null,
            MaxOutputTokens = o.MaxOutputTokens,
            Temperature = o.Temperature,
            TopP = o.TopP,
            TopK = o.TopK,
            PresencePenalty = o.PresencePenalty,
            FrequencyPenalty = o.FrequencyPenalty,
            StopSequences = o.StopSequences,
            Seed = o.Seed,
            ProviderOptions = prepared?.ProviderOptions ?? o.ProviderOptions,
            Headers = o.Headers,
        };

        return new StepPlan(model, request, tools, sent);
    }

    private static List<Tool> ActiveTools(GenerateTextOptions o, IReadOnlyList<string>? activeNames) =>
        (o.Tools ?? []).Where(t => activeNames is null || activeNames.Contains(t.Name)).ToList();

    // ------------------------------------------------------------------
    // Ejecución de herramientas (con reparación y aprobación)
    // ------------------------------------------------------------------

    public static async Task<ToolPhase> ExecuteToolsAsync(
        IReadOnlyList<ToolCallPart> calls,
        StepPlan plan,
        CallSettings settings,
        int stepNumber,
        CancellationToken cancellationToken,
        Action<StreamPart>? emit = null)
    {
        var processed = await RunLimitedAsync(
            calls,
            settings.MaxParallelToolCalls,
            call => ProcessCallAsync(call, plan, settings, stepNumber, emit, preApproved: false, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new ToolPhase(
            processed.Select(p => p.Call).ToList(),
            processed.Select(p => p.Result).OfType<ToolResultPart>().ToList(),
            processed.Select(p => p.PendingApproval).OfType<ToolApprovalRequest>().ToList());
    }

    /// <summary>Ejecuta en paralelo con un máximo de <paramref name="limit"/> tareas a la vez, conservando el orden.</summary>
    private static async Task<T[]> RunLimitedAsync<T>(IReadOnlyList<ToolCallPart> calls, int limit, Func<ToolCallPart, Task<T>> run, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, limit));
        return await Task.WhenAll(calls.Select(async call =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await run(call).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    private static async Task<ProcessedCall> ProcessCallAsync(
        ToolCallPart call,
        StepPlan plan,
        CallSettings settings,
        int stepNumber,
        Action<StreamPart>? emit,
        bool preApproved,
        CancellationToken cancellationToken)
    {
        if (call.ProviderExecuted)
        {
            return new(call, null); // El proveedor ya la ejecutó; su resultado viene en el contenido del modelo.
        }

        // Las herramientas de servidor sin Execute las ejecuta el proveedor; las que tienen Execute
        // (herramientas definidas por el proveedor pero ejecutadas localmente) se tratan como locales.
        Tool? Find(ToolCallPart c) => plan.Tools.FirstOrDefault(t => t.Name == c.ToolName && (!t.IsProviderTool || t.Execute is not null));

        // Una llamada ya aprobada nunca se repara: la reparación cambiaría la operación que el usuario autorizó.
        ValueTask<ToolCallPart?> Repair(ToolCallRepairReason reason, string error) =>
            preApproved ? ValueTask.FromResult<ToolCallPart?>(null) : TryRepairAsync(call, reason, error, plan, settings, cancellationToken);

        var tool = Find(call);
        if (tool is null)
        {
            var error = $"Herramienta desconocida '{call.ToolName}'. Disponibles: {string.Join(", ", plan.Tools.Select(t => t.Name))}.";
            var repaired = await Repair(ToolCallRepairReason.UnknownTool, error).ConfigureAwait(false);
            if (repaired is null || Find(repaired) is null)
            {
                return new(call, Error(call, error));
            }

            call = repaired;
            tool = Find(call)!;
        }

        if (tool.Execute is null)
        {
            return new(call, null); // Herramienta de cliente: la resuelve el llamador.
        }

        if (call.InvalidInput)
        {
            const string error = "La entrada de la herramienta no es JSON válido.";
            var repaired = await Repair(ToolCallRepairReason.InvalidJson, error).ConfigureAwait(false);
            if (repaired is null || repaired.InvalidInput)
            {
                return new(call, Error(call, error));
            }

            call = repaired;
        }

        if (tool.ValidateInput?.Invoke(call.Input) is { } validationError)
        {
            var repaired = await Repair(ToolCallRepairReason.InvalidInput, validationError).ConfigureAwait(false);
            if (repaired is null || tool.ValidateInput(repaired.Input) is not null)
            {
                return new(call, Error(call, $"Entrada inválida: {validationError}"));
            }

            call = repaired;
        }

        if (!preApproved && tool.NeedsApproval is not null && await tool.NeedsApproval(call).ConfigureAwait(false))
        {
            if (settings.OnToolApproval is { } approve)
            {
                if (!await approve(new ToolApprovalRequest(call, tool)).ConfigureAwait(false))
                {
                    return new(call, Error(call, DeniedMessage));
                }
            }
            else if (settings.DeferToolApprovals)
            {
                // Aprobación diferida: el bucle se detiene y la llamada se resuelve con GenerateTextOptions.ToolApprovals.
                return new(call, null, new ToolApprovalRequest(call, tool));
            }
            else
            {
                // Falla cerrado: nunca se ejecuta sin aprobación y nunca queda una llamada sin resultado (que haría
                // fallar en el proveedor cualquier petición posterior con ese historial).
                return new(call, Error(call, $"La herramienta '{call.ToolName}' requiere aprobación y no hay un manejador de aprobación configurado (OnToolApproval); no se ejecutó."));
            }
        }

        using var activity = AiTelemetry.ActivitySource.StartActivity("ai.toolCall");
        activity?.SetTag("ai.toolCall.name", call.ToolName);
        activity?.SetTag("ai.toolCall.id", call.ToolCallId);
        try
        {
            var output = await tool.Execute(call.Input, new ToolExecutionContext
            {
                ToolCallId = call.ToolCallId,
                ToolName = call.ToolName,
                Messages = plan.SentMessages,
                StepNumber = stepNumber,
                Services = settings.Services,
                Context = settings.Context,
                Emit = emit,
                OnToolApproval = settings.OnToolApproval,
            }, cancellationToken).ConfigureAwait(false);

            return new(call, new ToolResultPart(call.ToolCallId, call.ToolName, Tool.SerializeOutput(output, tool.SerializerOptions)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new(call, Error(call, ex.Message));
        }
    }

    /// <summary>Resultado que recibe el modelo cuando el usuario deniega una llamada.</summary>
    internal const string DeniedMessage = "El usuario denegó la ejecución de la herramienta.";

    private const string NoDecisionMessage = "La llamada requería aprobación y no hubo una decisión válida al reanudar; no se ejecutó.";

    /// <summary>Resultado de error de una llamada denegada, con el motivo opcional del usuario.</summary>
    internal static ToolResultPart Denied(ToolCallPart call, string? reason) =>
        Error(call, reason is { Length: > 0 } ? $"{DeniedMessage} Motivo: {reason}" : DeniedMessage);

    /// <summary>
    /// Reanudación de aprobaciones diferidas: resuelve con <see cref="GenerateTextOptions.ToolApprovals"/> las llamadas
    /// sin resultado del último mensaje del asistente y devuelve el mensaje de herramienta con sus resultados.
    /// </summary>
    public static async Task<ApprovalResolution?> ResolveApprovalsAsync(GenerateTextOptions o, List<ModelMessage> conversation, Action<StreamPart>? emit, CancellationToken cancellationToken)
    {
        if (o.ToolApprovals is not { } approvals)
        {
            return null;
        }

        var index = conversation.FindLastIndex(m => m.Role != ChatRole.Tool);
        if (index < 0 || conversation[index].Role != ChatRole.Assistant)
        {
            if (approvals.Count == 0)
            {
                return null;
            }

            throw new ArgumentException(
                "ToolApprovals requiere que los mensajes terminen en el mensaje del asistente con las llamadas pendientes (opcionalmente seguido de resultados de herramientas).",
                nameof(o));
        }

        var answered = conversation.Skip(index + 1)
            .SelectMany(m => m.Content.OfType<ToolResultPart>())
            .Select(r => r.ToolCallId)
            .ToHashSet(StringComparer.Ordinal);
        var tools = ActiveTools(o, o.ActiveTools);
        var pending = conversation[index].Content.OfType<ToolCallPart>()
            .Where(c => !c.ProviderExecuted && !answered.Contains(c.ToolCallId))
            // Las de cliente (sin Execute) las resuelve el llamador. Las de herramientas que ya no existen se cierran con
            // un error en vez de quedar sin resultado (lo que haría fallar la petición al proveedor).
            .Where(c => !tools.Any(t => t.Name == c.ToolName && t.Execute is null))
            .DistinctBy(c => c.ToolCallId, StringComparer.Ordinal) // una llamada repetida no se ejecuta dos veces
            .ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        var decisions = approvals.GroupBy(a => a.ToolCallId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var plan = new StepPlan(o.Model, new LanguageModelRequest { Messages = conversation }, tools, conversation);
        var processed = await RunLimitedAsync(
            pending,
            o.MaxParallelToolCalls,
            call => decisions.TryGetValue(call.ToolCallId, out var decision)
                ? decision.Approved
                    ? ProcessCallAsync(call, plan, o, stepNumber: 0, emit, preApproved: true, cancellationToken)
                    : Task.FromResult(new ProcessedCall(call, Denied(call, decision.Reason)))
                : Task.FromResult(new ProcessedCall(call, Error(call, NoDecisionMessage))),
            cancellationToken).ConfigureAwait(false);

        var results = processed.Select(p => p.Result).OfType<ToolResultPart>().ToArray();
        if (results.Length == 0)
        {
            return null;
        }

        var parts = results
            .Select(r => decisions.TryGetValue(r.ToolCallId, out var d) && !d.Approved
                ? (StreamPart)new ToolOutputDeniedStreamPart(r)
                : new ToolResultStreamPart(r))
            .ToList();
        return new ApprovalResolution(ModelMessage.Tool(results), parts);
    }

    private static async ValueTask<ToolCallPart?> TryRepairAsync(
        ToolCallPart call,
        ToolCallRepairReason reason,
        string error,
        StepPlan plan,
        CallSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.RepairToolCall is null)
        {
            return null;
        }

        try
        {
            var repaired = await settings.RepairToolCall(new ToolCallRepairContext(call, reason, error, plan.Tools, plan.SentMessages)
            {
                CancellationToken = cancellationToken,
            }).ConfigureAwait(false);
            // La reparación conserva siempre el id original para que el resultado empareje.
            return repaired is null ? null : repaired with { ToolCallId = call.ToolCallId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static ToolResultPart Error(ToolCallPart call, string message) =>
        new(call.ToolCallId, call.ToolName, Tool.ErrorOutput(message)) { IsError = true };

    // ------------------------------------------------------------------
    // Pasos
    // ------------------------------------------------------------------

    public static StepResult BuildStep(int stepNumber, StepPlan plan, LanguageModelResult result, ToolPhase tools, ModelMessage? resolvedApprovals = null)
    {
        // Las llamadas reparadas reemplazan a las originales en el contenido.
        var repaired = tools.Calls.ToDictionary(c => c.ToolCallId);
        var modelContent = result.Content
            .Select(p => p is ToolCallPart c && repaired.TryGetValue(c.ToolCallId, out var fixedCall) ? fixedCall : p)
            .ToList();

        // Al reanudar aprobaciones, sus resultados van antes que la respuesta del modelo (para persistirlos en orden).
        var responseMessages = resolvedApprovals is null ? new List<ModelMessage>() : [resolvedApprovals];
        var assistantContent = modelContent
            .Where(p => p is not TextPart { Text.Length: 0 })
            .ToList();
        if (assistantContent.Count > 0)
        {
            responseMessages.Add(new ModelMessage(ChatRole.Assistant, assistantContent));
        }

        if (tools.Results.Count > 0)
        {
            responseMessages.Add(new ModelMessage(ChatRole.Tool, tools.Results));
        }

        return new StepResult
        {
            StepNumber = stepNumber,
            Content = [.. modelContent, .. tools.Results],
            FinishReason = result.FinishReason,
            Usage = result.Usage,
            Request = plan.Request,
            Response = result.Response,
            ResponseMessages = responseMessages,
            Warnings = result.Warnings,
            PendingApprovals = tools.PendingApprovals,
        };
    }

    public static bool ShouldContinue(GenerateTextOptions o, StepResult step, List<StepResult> steps)
    {
        var clientCalls = step.ToolCalls.Where(c => !c.ProviderExecuted).ToList();
        var clientResults = step.ToolResults.Count(r => !r.ProviderExecuted);
        return clientCalls.Count > 0
               && clientResults == clientCalls.Count
               && !(o.StopWhen ?? DefaultStop).IsMet(steps);
    }

    public static Lazy<object?>? OutputFor(GenerateTextOptions o, List<StepResult> steps) =>
        o.Output is null ? null : new Lazy<object?>(() => o.Output.ParseUntyped(steps[^1].Text));

    private static List<ToolCallPart> ToolCallsOf(LanguageModelResult result) => result.Content.OfType<ToolCallPart>().ToList();

    // ------------------------------------------------------------------
    // generateText
    // ------------------------------------------------------------------

    public static async Task<GenerateTextResult> GenerateAsync(GenerateTextOptions o)
    {
        var ct = o.CancellationToken;
        using var root = AiTelemetry.ActivitySource.StartActivity("ai.generateText");
        root?.SetTag("ai.model.provider", o.Model.Provider);
        root?.SetTag("ai.model.id", o.Model.ModelId);

        var steps = new List<StepResult>();
        try
        {
            var conversation = InitialMessages(o);
            var resolved = (await ResolveApprovalsAsync(o, conversation, null, ct).ConfigureAwait(false))?.Message;
            if (resolved is not null)
            {
                conversation.Add(resolved);
            }

            while (true)
            {
                var plan = await PrepareAsync(o, conversation, steps).ConfigureAwait(false);
                LanguageModelResult result;
                using (var stepActivity = AiTelemetry.ActivitySource.StartActivity("ai.generateText.step"))
                {
                    result = await Retry.ExecuteAsync(c => plan.Model.GenerateAsync(plan.Request, c), o.MaxRetries, ct, o.MaxRetryAfter).ConfigureAwait(false);
                    stepActivity?.SetTag("ai.response.finishReason", result.FinishReason.ToString());
                    stepActivity?.SetTag("ai.usage.inputTokens", result.Usage.InputTokens);
                    stepActivity?.SetTag("ai.usage.outputTokens", result.Usage.OutputTokens);
                }

                var toolPhase = await ExecuteToolsAsync(ToolCallsOf(result), plan, o, steps.Count, ct).ConfigureAwait(false);
                var step = BuildStep(steps.Count, plan, result, toolPhase, steps.Count == 0 ? resolved : null);
                steps.Add(step);
                conversation.AddRange(steps.Count == 1 && resolved is not null ? step.ResponseMessages.Skip(1) : step.ResponseMessages);

                if (o.OnStepFinish is not null)
                {
                    await o.OnStepFinish(step).ConfigureAwait(false);
                }

                if (!ShouldContinue(o, step, steps))
                {
                    break;
                }
            }

            var final = new GenerateTextResult(steps, OutputFor(o, steps));
            if (o.OnFinish is not null)
            {
                await o.OnFinish(final).ConfigureAwait(false);
            }

            return final;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (o.OnAbort is not null)
            {
                await o.OnAbort(steps).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            root?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (o.OnError is not null)
            {
                await o.OnError(ex).ConfigureAwait(false);
            }

            throw;
        }
    }

    // ------------------------------------------------------------------
    // streamText
    // ------------------------------------------------------------------

    public static async Task RunStreamAsync(GenerateTextOptions o, ReplayBuffer<StreamPart> buffer, TaskCompletionSource<GenerateTextResult> completion)
    {
        var ct = o.CancellationToken;
        using var root = AiTelemetry.ActivitySource.StartActivity("ai.streamText");
        root?.SetTag("ai.model.provider", o.Model.Provider);
        root?.SetTag("ai.model.id", o.Model.ModelId);

        async ValueTask Emit(StreamPart part)
        {
            buffer.Add(part);
            if (o.OnChunk is not null && IsChunk(part))
            {
                await o.OnChunk(part).ConfigureAwait(false);
            }
        }

        var steps = new List<StepResult>();
        try
        {
            var conversation = InitialMessages(o);
            await Emit(new StartPart()).ConfigureAwait(false);
            var resolution = await ResolveApprovalsAsync(o, conversation, part => buffer.Add(part), ct).ConfigureAwait(false);
            var resolved = resolution?.Message;
            if (resolution is not null)
            {
                conversation.Add(resolution.Message);
                foreach (var part in resolution.Parts)
                {
                    await Emit(part).ConfigureAwait(false);
                }
            }

            while (true)
            {
                var plan = await PrepareAsync(o, conversation, steps).ConfigureAwait(false);
                await Emit(new StartStepPart(steps.Count)).ConfigureAwait(false);

                var result = await StreamStepAsync(plan, o, Emit, ct).ConfigureAwait(false);

                // Las herramientas pueden emitir datos propios (WriteData) mientras se ejecutan.
                void EmitFromTool(StreamPart part) => buffer.Add(part);
                var toolPhase = await ExecuteToolsAsync(ToolCallsOf(result), plan, o, steps.Count, ct, EmitFromTool).ConfigureAwait(false);
                foreach (var r in toolPhase.Results)
                {
                    await Emit(new ToolResultStreamPart(r)).ConfigureAwait(false);
                }

                foreach (var approval in toolPhase.PendingApprovals)
                {
                    await Emit(new ToolApprovalRequestStreamPart(approval)).ConfigureAwait(false);
                }

                var step = BuildStep(steps.Count, plan, result, toolPhase, steps.Count == 0 ? resolved : null);
                steps.Add(step);
                conversation.AddRange(steps.Count == 1 && resolved is not null ? step.ResponseMessages.Skip(1) : step.ResponseMessages);
                await Emit(new FinishStepPart(step)).ConfigureAwait(false);

                if (o.OnStepFinish is not null)
                {
                    await o.OnStepFinish(step).ConfigureAwait(false);
                }

                if (!ShouldContinue(o, step, steps))
                {
                    break;
                }
            }

            var final = new GenerateTextResult(steps, OutputFor(o, steps));
            await Emit(new FinishPart(final.FinishReason, final.TotalUsage)).ConfigureAwait(false);
            if (o.OnFinish is not null)
            {
                await o.OnFinish(final).ConfigureAwait(false);
            }

            completion.TrySetResult(final);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            buffer.Add(new AbortPart());
            if (o.OnAbort is not null)
            {
                await SafeInvoke(() => o.OnAbort(steps)).ConfigureAwait(false);
            }

            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            root?.SetStatus(ActivityStatusCode.Error, ex.Message);
            buffer.Add(new ErrorPart(ex));
            if (o.OnError is not null)
            {
                await SafeInvoke(() => o.OnError(ex)).ConfigureAwait(false);
            }

            completion.TrySetException(ex);
        }
        finally
        {
            buffer.Complete();
        }
    }

    private static bool IsChunk(StreamPart part) => part is TextDeltaPart or ReasoningDeltaPart or ToolInputStartPart
        or ToolInputDeltaPart or ToolCallStreamPart or ToolResultStreamPart or ToolApprovalRequestStreamPart or ToolOutputDeniedStreamPart
        or SourceStreamPart or ContentStreamPart or DataStreamPart;

    private static async ValueTask SafeInvoke(Func<ValueTask> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Un fallo en el callback de error/abort no debe ocultar el error original.
        }
    }

    private static async Task<LanguageModelResult> StreamStepAsync(
        StepPlan plan,
        GenerateTextOptions o,
        Func<StreamPart, ValueTask> emit,
        CancellationToken ct)
    {
        var maxRetries = o.MaxRetries;
        var transform = o.StreamTransform;
        for (var attempt = 0; ; attempt++)
        {
            var accumulator = new StreamAccumulator();
            var emittedAny = false;
            string? openText = null;
            string? openReasoning = null;

            async ValueTask Close()
            {
                if (openText is not null)
                {
                    await emit(new TextEndPart(openText)).ConfigureAwait(false);
                    openText = null;
                }

                if (openReasoning is not null)
                {
                    await emit(new ReasoningEndPart(openReasoning)).ConfigureAwait(false);
                    openReasoning = null;
                }
            }

            try
            {
                var source = plan.Model.StreamAsync(plan.Request, ct);
                if (transform is not null)
                {
                    source = transform(source);
                }

                await foreach (var part in source.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (part is ErrorPart error)
                    {
                        throw error.Error;
                    }

                    accumulator.Add(part);
                    switch (part)
                    {
                        case TextDeltaPart t:
                            if (openText != t.Id)
                            {
                                await Close().ConfigureAwait(false);
                                await emit(new TextStartPart(t.Id)).ConfigureAwait(false);
                                openText = t.Id;
                            }

                            await emit(part).ConfigureAwait(false);
                            break;
                        case ReasoningDeltaPart r:
                            if (openReasoning != r.Id)
                            {
                                await Close().ConfigureAwait(false);
                                await emit(new ReasoningStartPart(r.Id)).ConfigureAwait(false);
                                openReasoning = r.Id;
                            }

                            await emit(part).ConfigureAwait(false);
                            break;
                        case ReasoningEndPart e:
                            if (openReasoning != e.Id)
                            {
                                await emit(new ReasoningStartPart(e.Id)).ConfigureAwait(false);
                            }

                            openReasoning = null;
                            await emit(part).ConfigureAwait(false);
                            break;
                        case ModelFinishPart:
                        case ResponseMetadataPart:
                            break;
                        case SourceStreamPart:
                            await emit(part).ConfigureAwait(false);
                            break;
                        default:
                            await Close().ConfigureAwait(false);
                            await emit(part).ConfigureAwait(false);
                            break;
                    }

                    emittedAny = true;
                }

                await Close().ConfigureAwait(false);
                return accumulator.Build();
            }
            catch (Exception ex) when (!emittedAny && attempt < maxRetries && Retry.IsRetryable(ex) && !ct.IsCancellationRequested
                                       && Retry.TryGetDelay(attempt, ex, o.MaxRetryAfter, out var delay))
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}
