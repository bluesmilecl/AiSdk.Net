using AiSdk.Documents;
using AiSdk.Mistral;

namespace AiSdk.IntegrationTests;

/// <summary>OCR con modelos reales (se omiten sin API key).</summary>
public class DocumentLiveTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [SkippableTheory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    [InlineData("google")]
    public async Task VisionModelReadsReceipt(string provider)
    {
        var ocr = new VisionModelOcr(LiveProviderTests.Model(provider));
        var result = await ocr.RecognizeAsync(new OcrRequest { Data = await File.ReadAllBytesAsync(Fixture("receipt.png")), MediaType = "image/png" });

        Assert.Contains("12.345", result.Markdown);
    }

    [SkippableFact]
    public async Task MistralOcrReadsScannedPdf()
    {
        Skip.IfNot(LiveProviderTests.Has("MISTRAL_API_KEY"), "MISTRAL_API_KEY no configurada");
        var converter = new MarkdownConverter(new() { Ocr = MistralProvider.Default.Ocr() });

        var result = await converter.ConvertFileAsync(Fixture("scanned.pdf"));

        Assert.Contains("CONTRATO", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PageTextSource.Ocr, result.Pages[0].Source);
    }
}
