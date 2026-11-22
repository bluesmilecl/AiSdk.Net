using System.ComponentModel;
using System.Text.Json;
using AiSdk.Internal;

namespace AiSdk.Tests;

[Description("Una receta de cocina")]
public sealed record Recipe(
    [property: Description("Nombre de la receta")] string Name,
    List<string> Ingredients,
    int? Minutes);

public enum Sentiment
{
    Positive,
    Negative,
}

public class StreamAndObjectTests
{
    [Fact]
    public async Task StreamsTextAndExposesFinalResult()
    {
        var model = new MockLanguageModel { StreamChunkSize = 3 }.ReturnsText("Hola desde .NET");

        var stream = Ai.StreamText(model, "hola", TestContext.Current.CancellationToken);

        var chunks = new List<string>();
        await foreach (var c in stream.TextStream)
        {
            chunks.Add(c);
        }

        Assert.True(chunks.Count > 1);
        Assert.Equal("Hola desde .NET", string.Concat(chunks));
        Assert.Equal("Hola desde .NET", await stream.Text);
        Assert.Equal(FinishReason.Stop, await stream.FinishReason);
    }

    [Fact]
    public async Task FullStreamContainsLifecycleAndToolParts()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("echo", new { city = "x" }, "c1")
            .ReturnsText("listo");
        var echo = Tool.Create<WeatherInput, string>("echo", "eco", i => i.City);

        var stream = Ai.StreamText(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [echo],
            StopWhen = StopCondition.StepCountIs(3),
        });

        var types = new List<string>();
        await foreach (var part in stream.FullStream)
        {
            types.Add(part.GetType().Name);
        }

        // Un segundo consumidor recibe lo mismo (el stream es re-consumible).
        var again = 0;
        await foreach (var _ in stream.FullStream)
        {
            again++;
        }

        Assert.Equal(types.Count, again);
        Assert.Equal(nameof(StartPart), types[0]);
        Assert.Equal(nameof(FinishPart), types[^1]);
        Assert.Contains(nameof(ToolInputStartPart), types);
        Assert.Contains(nameof(ToolCallStreamPart), types);
        Assert.Contains(nameof(ToolResultStreamPart), types);
        Assert.Contains(nameof(TextStartPart), types);
        Assert.Contains(nameof(TextEndPart), types);
        Assert.Equal(2, types.Count(t => t == nameof(StartStepPart)));
        Assert.Equal("listo", await stream.Text);
        Assert.Equal(2, (await stream.Steps).Count);
    }

    [Fact]
    public async Task StreamErrorsSurfaceInFullStreamAndTasks()
    {
        var model = new MockLanguageModel().ReturnsError(new ApiCallException("bad", 400, null, null));

        var stream = Ai.StreamText(model, "x", TestContext.Current.CancellationToken);

        StreamPart? last = null;
        await foreach (var p in stream.FullStream)
        {
            last = p;
        }

        Assert.IsType<ErrorPart>(last);
        await Assert.ThrowsAsync<ApiCallException>(() => stream.Text);
        await Assert.ThrowsAsync<ApiCallException>(async () =>
        {
            await foreach (var _ in stream.TextStream)
            {
            }
        });
    }

    [Fact]
    public async Task GeneratesTypedObject()
    {
        var model = new MockLanguageModel().ReturnsText("""{"name":"Pebre","ingredients":["tomate","cebolla"],"minutes":10}""");

        var result = await Ai.GenerateObjectAsync<Recipe>(new GenerateObjectOptions { Model = model, Prompt = "Receta chilena" });

        Assert.Equal("Pebre", result.Object.Name);
        Assert.Equal(2, result.Object.Ingredients.Count);
        var format = model.Requests[0].ResponseFormat!;
        Assert.Equal(ResponseFormatType.Json, format.Type);
        Assert.Equal("Recipe", format.Name);
    }

    [Fact]
    public async Task GenerateObjectToleratesMarkdownFences()
    {
        var model = new MockLanguageModel().ReturnsText("```json\n{\"name\":\"A\",\"ingredients\":[],\"minutes\":null}\n```");

        var result = await Ai.GenerateObjectAsync<Recipe>(new GenerateObjectOptions { Model = model, Prompt = "x" });

        Assert.Equal("A", result.Object.Name);
    }

    [Fact]
    public async Task InvalidObjectThrowsNoObjectGenerated()
    {
        var model = new MockLanguageModel().ReturnsText("esto no es json");

        var ex = await Assert.ThrowsAsync<NoObjectGeneratedException>(() =>
            Ai.GenerateObjectAsync<Recipe>(new GenerateObjectOptions { Model = model, Prompt = "x" }));

        Assert.Equal("esto no es json", ex.Text);
    }

    [Fact]
    public async Task StreamsPartialObjects()
    {
        var model = new MockLanguageModel { StreamChunkSize = 5 }
            .ReturnsText("""{"name":"Pebre","ingredients":["tomate","cebolla","cilantro"],"minutes":10}""");

        var stream = Ai.StreamObject<Recipe>(new GenerateObjectOptions { Model = model, Prompt = "x" });

        var partials = new List<Recipe>();
        await foreach (var p in stream.PartialObjectStream)
        {
            partials.Add(p);
        }

        Assert.True(partials.Count > 2);
        Assert.Equal(3, partials[^1].Ingredients.Count);
        Assert.Equal(10, (await stream.Object).Minutes);
    }

    [Fact]
    public async Task ArrayAndChoiceOutputs()
    {
        var arrayModel = new MockLanguageModel().ReturnsText("""{"elements":[{"city":"Lima"},{"city":"Quito"}]}""");
        var arrayResult = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = arrayModel,
            Prompt = "x",
            Output = Output.Array<WeatherInput>(),
        });
        Assert.Equal(["Lima", "Quito"], arrayResult.GetOutput<IReadOnlyList<WeatherInput>>().Select(w => w.City));

        var choiceModel = new MockLanguageModel().ReturnsText("""{"result":"negativo"}""");
        var choiceResult = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = choiceModel,
            Prompt = "x",
            Output = Output.Choice("positivo", "negativo"),
        });
        Assert.Equal("negativo", choiceResult.GetOutput<string>());
        var schema = choiceModel.Requests[0].ResponseFormat!.Schema!.Value;
        Assert.Equal(2, schema.GetProperty("properties").GetProperty("result").GetProperty("enum").GetArrayLength());
    }

    [Fact]
    public async Task OutputWorksTogetherWithTools()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Lima" })
            .ReturnsText("""{"city":"Lima","temperatureC":21}""");
        var tool = Tool.Create<WeatherInput, WeatherOutput>("get_weather", "x", i => new WeatherOutput(i.City, 21));

        var result = await Ai.GenerateTextAsync(new GenerateTextOptions
        {
            Model = model,
            Prompt = "x",
            Tools = [tool],
            Output = Output.Object<WeatherOutput>(),
            StopWhen = StopCondition.StepCountIs(3),
        });

        Assert.Equal(21, result.GetOutput<WeatherOutput>().TemperatureC);
    }

    [Fact]
    public void SchemaIncludesDescriptionsNullabilityAndEnums()
    {
        var schema = JsonSchema.For<Recipe>();
        Assert.Equal("Una receta de cocina", schema.GetProperty("description").GetString());
        var props = schema.GetProperty("properties");
        Assert.Equal("Nombre de la receta", props.GetProperty("name").GetProperty("description").GetString());
        Assert.Equal("array", props.GetProperty("ingredients").GetProperty("type").GetString());

        var enumSchema = JsonSchema.For<Sentiment>();
        Assert.Equal(2, enumSchema.GetProperty("enum").GetArrayLength());

        var strict = JsonSchema.ToStrict(schema);
        Assert.False(strict.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(3, strict.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public async Task ToolFromDelegateBuildsSchemaAndInjectsParameters()
    {
        var tool = Tool.FromDelegate(
            "add",
            "Suma dos números",
            ([Description("Primer número")] int a, int b, CancellationToken ct, int scale = 1) => Task.FromResult((a + b) * scale));

        var props = tool.InputSchema.GetProperty("properties");
        Assert.Equal("Primer número", props.GetProperty("a").GetProperty("description").GetString());
        Assert.False(props.TryGetProperty("ct", out _));
        Assert.Equal(["a", "b"], tool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));

        var output = await tool.Execute!(
            JsonElement.Parse("""{"a":2,"b":3}"""),
            new ToolExecutionContext { ToolCallId = "1", ToolName = "add", Messages = [] },
            CancellationToken.None);
        Assert.Equal(5, output);
    }

    [Theory]
    [InlineData("""{"name":"Pe""", "Pe")]
    [InlineData("""{"name":"Pebre","ingr""", "Pebre")]
    [InlineData("""{"name":"Pebre","ingredients":["to""", "Pebre")]
    public void ParsesPartialJson(string partial, string expectedName)
    {
        var node = JsonUtil.ParsePartial(partial);
        Assert.Equal(expectedName, (string?)node?["name"]);
    }
}
