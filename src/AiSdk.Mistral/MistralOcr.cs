using System.Text.Json.Nodes;
using AiSdk.Http;

namespace AiSdk.Mistral;

/// <summary>
/// OCR de Mistral (<c>POST /v1/ocr</c>). Acepta imágenes y PDF (incluidos escaneados) y devuelve
/// Markdown por página conservando tablas y estructura.
/// </summary>
public sealed class MistralOcr(MistralProviderOptions options, string modelId = "mistral-ocr-latest") : IOcrEngine
{
    public string Name => modelId;

    public bool Supports(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
    {
        var dataUrl = $"data:{request.MediaType};base64,{Convert.ToBase64String(request.Data)}";
        var document = request.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject { ["type"] = "image_url", ["image_url"] = dataUrl }
            : new JsonObject { ["type"] = "document_url", ["document_url"] = dataUrl };
        var body = new JsonObject { ["model"] = modelId, ["document"] = document, ["include_image_base64"] = false };

        var apiKey = ProviderHttp.LoadApiKey(options.ApiKey, "MISTRAL_API_KEY", "Mistral");
        var headers = new Dictionary<string, string>(options.Headers) { ["Authorization"] = $"Bearer {apiKey}" };
        var http = ProviderHttp.CreateJsonRequest(new Uri($"{options.BaseUrl.TrimEnd('/')}/ocr"), body, headers, stream: false);
        var json = await ProviderHttp.SendJsonAsync(options.HttpClient ?? ProviderHttp.DefaultClient, http, cancellationToken).ConfigureAwait(false);

        var pages = (json["pages"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(p => new OcrPage(((int?)p["index"] ?? 0) + 1, (string?)p["markdown"] ?? string.Empty))
            .ToList();
        return new OcrResult(pages);
    }
}
