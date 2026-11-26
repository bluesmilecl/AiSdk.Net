using System.Globalization;
using System.Text.Json;
using AiSdk.Tests.Resources;

namespace AiSdk.Tests;

/// <summary>Mensajes del núcleo (los que recibe el modelo y los de esquemas) en inglés por defecto y en español con "es*".</summary>
public class CoreLocalizationTests
{
    private static T WithCulture<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("", "The user denied the execution of the tool. Reason: no")]
    [InlineData("es-CL", "El usuario denegó la ejecución de la herramienta. Motivo: no")]
    public async Task TheDenialTheModelReceivesFollowsTheCurrentUICulture(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            var model = new MockLanguageModel().ReturnsText("ok");
            await Ai.GenerateTextAsync(new GenerateTextOptions
            {
                Model = model,
                Messages =
                [
                    ModelMessage.User("borra"),
                    ModelMessage.Assistant(new ToolCallPart("c1", "delete", JsonElement.Parse("{}"))),
                ],
                Tools = [Tool.Create<JsonElement, string>("delete", "Deletes", _ => "deleted").RequireApproval()],
                ToolApprovals = [new ToolApprovalResponse("c1", Approved: false, Reason: "no")],
                CancellationToken = TestContext.Current.CancellationToken,
            });

            var result = model.Requests[0].Messages[^1].Content.OfType<ToolResultPart>().Single();
            Assert.True(result.IsError);
            Assert.Equal(expected, result.Output.GetString());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("", "City name.")]
    [InlineData("fr-FR", "City name.")]
    [InlineData("es-CL", "Nombre de la ciudad.")]
    public void LocalizedDescriptionsFollowTheCultureWhenTheSchemaIsGenerated(string culture, string expected)
    {
        var schema = WithCulture(culture, () => JsonSchema.For<WeatherQuery>());
        Assert.Equal(expected, schema.GetProperty("properties").GetProperty("city").GetProperty("description").GetString());
    }

    [Fact]
    public void LocalizedDescriptionFallsBackToTheResourceName()
    {
        var attribute = new LocalizedDescriptionAttribute(typeof(TestStrings), "DoesNotExist");
        Assert.Equal("DoesNotExist", attribute.Description);
        Assert.Equal("Nothing", new LocalizedDescriptionAttribute(typeof(string), "Nothing").Description); // sin ResourceManager
    }

    [Fact]
    public void VisionOcrInstructionsAndNoTextMarkerWorkInBothLanguages()
    {
        Assert.Contains("(no text)", WithCulture("", () => VisionModelOcr.DefaultInstructions), StringComparison.Ordinal);
        Assert.Contains("(sin texto)", WithCulture("es-CL", () => VisionModelOcr.DefaultInstructions), StringComparison.Ordinal);
        Assert.True(VisionModelOcr.IsNoTextReply(" (no text)\n"));
        Assert.True(VisionModelOcr.IsNoTextReply("(Sin texto)"));
        Assert.False(VisionModelOcr.IsNoTextReply("Factura 123"));
        Assert.False(VisionModelOcr.IsNoTextReply(null));
    }

    private sealed record WeatherQuery([property: LocalizedDescription(typeof(TestStrings), nameof(TestStrings.CityName))] string City);
}
