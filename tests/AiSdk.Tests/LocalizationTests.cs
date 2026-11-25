using System.Globalization;
using AiSdk.Cohere;

namespace AiSdk.Tests;

/// <summary>Mensajes en inglés por defecto y en español con una cultura de interfaz "es*" (ensamblado satélite).</summary>
public class LocalizationTests
{
    [Theory]
    [InlineData("", "The AiSdk Cohere provider only offers reranking and embeddings.")]
    [InlineData("en-US", "The AiSdk Cohere provider only offers reranking and embeddings.")]
    [InlineData("es-CL", "El proveedor Cohere de AiSdk solo ofrece reranking y embeddings.")]
    [InlineData("es", "El proveedor Cohere de AiSdk solo ofrece reranking y embeddings.")]
    [InlineData("fr-FR", "The AiSdk Cohere provider only offers reranking and embeddings.")]
    public void MessagesFollowTheCurrentUICulture(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal(expected, Assert.Throws<NotSupportedException>(() => CohereProvider.Default.LanguageModel("x")).Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
