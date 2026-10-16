using System.Diagnostics;
using System.Text.Json;
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

    public static async Task<List<ToolResultPart>> ExecuteToolsAsync(
        IReadOnlyList<ToolCallPart> calls,
        StepPlan plan,
        CallSettings settings,
        int stepNumber,
        CancellationToken cancellationToken)
    {
        var tasks = calls.Select(call => ExecuteToolAsync(call, plan, settings, stepNumber, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OfType<ToolResultPart>().ToList();
    }

    private static async Task<ToolResultPart?> ExecuteToolAsync(
        ToolCallPart call,
        StepPlan plan,
        CallSettings settings,
        int stepNumber,
        CancellationToken cancellationToken)
    {
        var tool = plan.Tools.FirstOrDefault(t => t.Name == call.ToolName);
        if (tool is null)
        {
            return Error(call, $"Herramienta desconocida '{call.ToolName}'. Disponibles: {string.Join(", ", plan.Tools.Select(t => t.Name))}.");
        }

        if (tool.Execute is null)
        {
            return null; // Herramienta de cliente: la resuelve el llamador.
        }

        if (call.InvalidInput)
        {
            return Error(call, "La entrada de la herramienta no es JSON válido.");
        }

        if (tool.NeedsApproval is not null && await tool.NeedsApproval(call).ConfigureAwait(false))
        {
            if (settings.OnToolApproval is null)
            {
                return null; // Queda pendiente de aprobación.
            }

            if (!await settings.OnToolApproval(new ToolApprovalRequest(call, tool)).ConfigureAwait(false))
            {
                return Error(call, "El usuario denegó la ejecución de la herramienta.");
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

            return new ToolResultPart(call.ToolCallId, call.ToolName, Tool.SerializeOutput(output, tool.SerializerOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return Error(call, ex is JsonException ? $"Entrada inválida: {ex.Message}" : ex.Message);
        }
    }

    private static ToolResultPart Error(ToolCallPart call, string message) =>
        new(call.ToolCallId, call.ToolName, Tool.ErrorOutput(message)) { IsError = true };

    public static StepResult BuildStep(int stepNumber, StepPlan plan, LanguageModelResult result, List<ToolResultPart> toolResults)
    {
        var responseMessages = new List<ModelMessage>();
        var assistantContent = result.Content
            .Where(p => p is not TextPart { Text.Length: 0 })
            .ToList();
        if (assistantContent.Count > 0)
        {
            responseMessages.Add(new ModelMessage(ChatRole.Assistant, assistantContent));
        }

        if (toolResults.Count > 0)
        {
            responseMessages.Add(new ModelMessage(ChatRole.Tool, toolResults));
        }

        return new StepResult
        {
            StepNumber = stepNumber,
            Content = [.. result.Content, .. toolResults],
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
        var calls = step.ToolCalls;
        return calls.Count > 0
               && step.ToolResults.Count == calls.Count
               && !(o.StopWhen ?? DefaultStop).IsMet(steps);
    }

    public static Lazy<object?>? OutputFor(GenerateTextOptions o, List<StepResult> steps) =>
        o.Output is null ? null : new Lazy<object?>(() => o.Output.ParseUntyped(steps[^1].Text));

    // ------------------------------------------------------------------
    // generateText
    // ------------------------------------------------------------------

    public static async Task<GenerateTextResult> GenerateAsync(GenerateTextOptions o)
    {
        var ct = o.CancellationToken;
        using var root = AiTelemetry.ActivitySource.StartActivity("ai.generateText");
        root?.SetTag("ai.model.provider", o.Model.Provider);
        root?.SetTag("ai.model.id", o.Model.ModelId);

        var conversation = InitialMessages(o);
        var steps = new List<StepResult>();
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

            var calls = result.Content.OfType<ToolCallPart>().ToList();
            var toolResults = await ExecuteToolsAsync(calls, plan, o, steps.Count, ct).ConfigureAwait(false);
            var step = BuildStep(steps.Count, plan, result, toolResults);
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

        return new GenerateTextResult(steps, OutputFor(o, steps));
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
        try
        {
            var conversation = InitialMessages(o);
            var steps = new List<StepResult>();
            buffer.Add(new StartPart());
            while (true)
            {
                var plan = await PrepareAsync(o, conversation, steps).ConfigureAwait(false);
                buffer.Add(new StartStepPart(steps.Count));

                var result = await StreamStepAsync(plan, o.MaxRetries, buffer, ct).ConfigureAwait(false);

                var calls = result.Content.OfType<ToolCallPart>().ToList();
                var toolResults = await ExecuteToolsAsync(calls, plan, o, steps.Count, ct).ConfigureAwait(false);
                foreach (var r in toolResults)
                {
                    buffer.Add(new ToolResultStreamPart(r));
                }

                var step = BuildStep(steps.Count, plan, result, toolResults);
                steps.Add(step);
                conversation.AddRange(step.ResponseMessages);
                buffer.Add(new FinishStepPart(step));

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
            buffer.Add(new FinishPart(final.FinishReason, final.TotalUsage));
            completion.TrySetResult(final);
        }
        catch (Exception ex)
        {
            root?.SetStatus(ActivityStatusCode.Error, ex.Message);
            buffer.Add(new ErrorPart(ex));
            completion.TrySetException(ex);
        }
        finally
        {
            buffer.Complete();
        }
    }

    private static async Task<LanguageModelResult> StreamStepAsync(StepPlan plan, int maxRetries, ReplayBuffer<StreamPart> buffer, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var accumulator = new StreamAccumulator();
            var emittedAny = false;
            string? openText = null;
            string? openReasoning = null;
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
                                Close(ref openText, ref openReasoning, buffer);
                                buffer.Add(new TextStartPart(t.Id));
                                openText = t.Id;
                            }

                            buffer.Add(part);
                            break;
                        case ReasoningDeltaPart r:
                            if (openReasoning != r.Id)
                            {
                                Close(ref openText, ref openReasoning, buffer);
                                buffer.Add(new ReasoningStartPart(r.Id));
                                openReasoning = r.Id;
                            }

                            buffer.Add(part);
                            break;
                        case ReasoningEndPart e:
                            if (openReasoning != e.Id)
                            {
                                buffer.Add(new ReasoningStartPart(e.Id));
                            }

                            openReasoning = null;
                            buffer.Add(part);
                            break;
                        case ModelFinishPart:
                        case ResponseMetadataPart:
                            break;
                        default:
                            Close(ref openText, ref openReasoning, buffer);
                            buffer.Add(part);
                            break;
                    }

                    emittedAny = true;
                }

                Close(ref openText, ref openReasoning, buffer);
                return accumulator.Build();
            }
            catch (Exception ex) when (!emittedAny && attempt < maxRetries && Retry.IsRetryable(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(Retry.Delay(attempt, ex), ct).ConfigureAwait(false);
            }
        }
    }

    private static void Close(ref string? openText, ref string? openReasoning, ReplayBuffer<StreamPart> buffer)
    {
        if (openText is not null)
        {
            buffer.Add(new TextEndPart(openText));
            openText = null;
        }

        if (openReasoning is not null)
        {
            buffer.Add(new ReasoningEndPart(openReasoning));
            openReasoning = null;
        }
    }
}
