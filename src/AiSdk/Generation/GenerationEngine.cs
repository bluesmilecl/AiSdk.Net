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

    internal sealed record ToolPhase(IReadOnlyList<ToolCallPart> Calls, List<ToolResultPart> Results);

    public static List<ModelMessage> InitialMessages(GenerateTextOptions o)
    {
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
        var activeNames = prepared?.ActiveTools ?? o.ActiveTools;
        var tools = (o.Tools ?? [])
            .Where(t => activeNames is null || activeNames.Contains(t.Name))
            .ToList();

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

    // ------------------------------------------------------------------
    // Ejecución de herramientas (con reparación y aprobación)
    // ------------------------------------------------------------------

    public static async Task<ToolPhase> ExecuteToolsAsync(
        IReadOnlyList<ToolCallPart> calls,
        StepPlan plan,
        CallSettings settings,
        int stepNumber,
        CancellationToken cancellationToken)
    {
        var processed = await Task.WhenAll(calls.Select(call => ProcessCallAsync(call, plan, settings, stepNumber, cancellationToken))).ConfigureAwait(false);
        return new ToolPhase(
            processed.Select(p => p.Call).ToList(),
            processed.Select(p => p.Result).OfType<ToolResultPart>().ToList());
    }

    private static async Task<(ToolCallPart Call, ToolResultPart? Result)> ProcessCallAsync(
        ToolCallPart call,
        StepPlan plan,
        CallSettings settings,
        int stepNumber,
        CancellationToken cancellationToken)
    {
        if (call.ProviderExecuted)
        {
            return (call, null); // El proveedor ya la ejecutó; su resultado viene en el contenido del modelo.
        }

        // Las herramientas de servidor sin Execute las ejecuta el proveedor; las que tienen Execute
        // (herramientas definidas por el proveedor pero ejecutadas localmente) se tratan como locales.
        Tool? Find(ToolCallPart c) => plan.Tools.FirstOrDefault(t => t.Name == c.ToolName && (!t.IsProviderTool || t.Execute is not null));

        var tool = Find(call);
        if (tool is null)
        {
            var error = $"Herramienta desconocida '{call.ToolName}'. Disponibles: {string.Join(", ", plan.Tools.Select(t => t.Name))}.";
            var repaired = await TryRepairAsync(call, ToolCallRepairReason.UnknownTool, error, plan, settings).ConfigureAwait(false);
            if (repaired is null || Find(repaired) is null)
            {
                return (call, Error(call, error));
            }

            call = repaired;
            tool = Find(call)!;
        }

        if (tool.Execute is null)
        {
            return (call, null); // Herramienta de cliente: la resuelve el llamador.
        }

        if (call.InvalidInput)
        {
            const string error = "La entrada de la herramienta no es JSON válido.";
            var repaired = await TryRepairAsync(call, ToolCallRepairReason.InvalidJson, error, plan, settings).ConfigureAwait(false);
            if (repaired is null || repaired.InvalidInput)
            {
                return (call, Error(call, error));
            }

            call = repaired;
        }

        if (tool.ValidateInput?.Invoke(call.Input) is { } validationError)
        {
            var repaired = await TryRepairAsync(call, ToolCallRepairReason.InvalidInput, validationError, plan, settings).ConfigureAwait(false);
            if (repaired is null || tool.ValidateInput(repaired.Input) is not null)
            {
                return (call, Error(call, $"Entrada inválida: {validationError}"));
            }

            call = repaired;
        }

        if (tool.NeedsApproval is not null && await tool.NeedsApproval(call).ConfigureAwait(false))
        {
            if (settings.OnToolApproval is null)
            {
                return (call, null); // Queda pendiente de aprobación.
            }

            if (!await settings.OnToolApproval(new ToolApprovalRequest(call, tool)).ConfigureAwait(false))
            {
                return (call, Error(call, "El usuario denegó la ejecución de la herramienta."));
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
            }, cancellationToken).ConfigureAwait(false);

            return (call, new ToolResultPart(call.ToolCallId, call.ToolName, Tool.SerializeOutput(output, tool.SerializerOptions)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return (call, Error(call, ex.Message));
        }
    }

    private static async ValueTask<ToolCallPart?> TryRepairAsync(
        ToolCallPart call,
        ToolCallRepairReason reason,
        string error,
        StepPlan plan,
        CallSettings settings)
    {
        if (settings.RepairToolCall is null)
        {
            return null;
        }

        try
        {
            var repaired = await settings.RepairToolCall(new ToolCallRepairContext(call, reason, error, plan.Tools, plan.SentMessages)).ConfigureAwait(false);
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

    public static StepResult BuildStep(int stepNumber, StepPlan plan, LanguageModelResult result, ToolPhase tools)
    {
        // Las llamadas reparadas reemplazan a las originales en el contenido.
        var repaired = tools.Calls.ToDictionary(c => c.ToolCallId);
        var modelContent = result.Content
            .Select(p => p is ToolCallPart c && repaired.TryGetValue(c.ToolCallId, out var fixedCall) ? fixedCall : p)
            .ToList();

        var responseMessages = new List<ModelMessage>();
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
            while (true)
            {
                var plan = await PrepareAsync(o, conversation, steps).ConfigureAwait(false);
                LanguageModelResult result;
                using (var stepActivity = AiTelemetry.ActivitySource.StartActivity("ai.generateText.step"))
                {
                    result = await Retry.ExecuteAsync(c => plan.Model.GenerateAsync(plan.Request, c), o.MaxRetries, ct).ConfigureAwait(false);
                    stepActivity?.SetTag("ai.response.finishReason", result.FinishReason.ToString());
                    stepActivity?.SetTag("ai.usage.inputTokens", result.Usage.InputTokens);
                    stepActivity?.SetTag("ai.usage.outputTokens", result.Usage.OutputTokens);
                }

                var toolPhase = await ExecuteToolsAsync(ToolCallsOf(result), plan, o, steps.Count, ct).ConfigureAwait(false);
                var step = BuildStep(steps.Count, plan, result, toolPhase);
                steps.Add(step);
                conversation.AddRange(step.ResponseMessages);

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
            while (true)
            {
                var plan = await PrepareAsync(o, conversation, steps).ConfigureAwait(false);
                await Emit(new StartStepPart(steps.Count)).ConfigureAwait(false);

                var result = await StreamStepAsync(plan, o.MaxRetries, Emit, ct).ConfigureAwait(false);

                var toolPhase = await ExecuteToolsAsync(ToolCallsOf(result), plan, o, steps.Count, ct).ConfigureAwait(false);
                foreach (var r in toolPhase.Results)
                {
                    await Emit(new ToolResultStreamPart(r)).ConfigureAwait(false);
                }

                var step = BuildStep(steps.Count, plan, result, toolPhase);
                steps.Add(step);
                conversation.AddRange(step.ResponseMessages);
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
        or ToolInputDeltaPart or ToolCallStreamPart or ToolResultStreamPart or SourceStreamPart or ContentStreamPart;

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
        int maxRetries,
        Func<StreamPart, ValueTask> emit,
        CancellationToken ct)
    {
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
                await foreach (var part in plan.Model.StreamAsync(plan.Request, ct).WithCancellation(ct).ConfigureAwait(false))
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
            catch (Exception ex) when (!emittedAny && attempt < maxRetries && Retry.IsRetryable(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(Retry.Delay(attempt, ex), ct).ConfigureAwait(false);
            }
        }
    }
}
