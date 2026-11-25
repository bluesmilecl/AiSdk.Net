using System.Diagnostics;
using AiSdk.Internal;

namespace AiSdk;

/// <summary>OpenTelemetry trace source of the SDK (<c>"AiSdk"</c>).</summary>
public static class AiTelemetry
{
    public const string SourceName = "AiSdk";

    /// <summary>
    /// Name of the <see cref="System.Diagnostics.Metrics.Meter"/> that records the OpenTelemetry GenAI client metrics
    /// (<c>gen_ai.client.operation.duration</c> and <c>gen_ai.client.token.usage</c>). Register it with
    /// <c>AddMeter(AiTelemetry.MeterName)</c>.
    /// </summary>
    public const string MeterName = "AiSdk";

    /// <summary>
    /// Source of the spans, which follow the OpenTelemetry GenAI semantic conventions. Its version is the package version.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName, GenAiTelemetry.Version);
}
