using System.ComponentModel;
using AiSdk.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace AiSdk.Tests;

public sealed class OrderAgent(ILanguageModel model) : Agent
{
    public override string? Description => "Consulta pedidos";

    protected override ILanguageModel Model => model;

    protected override string Instructions => "Eres el agente de pedidos.";

    protected override IEnumerable<Tool> Tools =>
    [
        Tool.FromDelegate("lookup_order", "Busca un pedido", ([Description("Id del pedido")] string orderId) => $"Pedido {orderId}: enviado"),
    ];
}

public class AgentTests
{
    [Fact]
    public async Task ToolLoopAgentRunsMultipleSteps()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("get_weather", new { city = "Lima" })
            .ReturnsToolCall("get_weather", new { city = "Quito" })
            .ReturnsText("Lima 21°C, Quito 21°C");

        var agent = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Instructions = "Eres un meteorólogo.",
            Tools = [Tool.Create<WeatherInput, WeatherOutput>("get_weather", "clima", i => new WeatherOutput(i.City, 21))],
        });

        var result = await agent.GenerateAsync("Clima en Lima y Quito", TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("Eres un meteorólogo.", model.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task AgentDefaultStopsAfterTwentySteps()
    {
        var model = new MockLanguageModel();
        for (var i = 0; i < 25; i++)
        {
            model.ReturnsToolCall("noop", new { city = "x" });
        }

        var agent = new ToolLoopAgent(new AgentSettings
        {
            Model = model,
            Tools = [Tool.Create<WeatherInput, string>("noop", "x", _ => "ok")],
        });

        var result = await agent.GenerateAsync("loop", TestContext.Current.CancellationToken);

        Assert.Equal(20, result.Steps.Count);
    }

    [Fact]
    public async Task ClassBasedAgentWorks()
    {
        var model = new MockLanguageModel()
            .ReturnsToolCall("lookup_order", new { orderId = "A1" })
            .ReturnsText("Tu pedido A1 fue enviado");

        var agent = new OrderAgent(model);
        var result = await agent.GenerateAsync("¿Dónde está A1?", TestContext.Current.CancellationToken);

        Assert.Equal("Tu pedido A1 fue enviado", result.Text);
        Assert.Equal("\"Pedido A1: enviado\"", result.Steps[0].ToolResults[0].Output.GetRawText());
    }

    [Fact]
    public async Task SessionRemembersConversation()
    {
        var model = new MockLanguageModel().ReturnsText("Hola Ana").ReturnsText("Te llamas Ana");
        var agent = new ToolLoopAgent(new AgentSettings { Model = model });
        var store = new InMemoryConversationStore();
        var session = agent.CreateSession("conv-1", store);

        await session.SendAsync("Me llamo Ana", TestContext.Current.CancellationToken);
        var second = await session.SendAsync("¿Cómo me llamo?", TestContext.Current.CancellationToken);

        Assert.Equal("Te llamas Ana", second.Text);
        Assert.Equal(3, model.Requests[1].Messages.Count);
        Assert.Equal(4, (await store.LoadAsync("conv-1", TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task StreamingSessionPersistsBeforeStreamEnds()
    {
        var model = new MockLanguageModel().ReturnsText("respuesta");
        var agent = new ToolLoopAgent(new AgentSettings { Model = model });
        var session = agent.CreateSession();

        var stream = await session.StreamAsync("hola", TestContext.Current.CancellationToken);
        await stream.ConsumeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, (await session.GetMessagesAsync(TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task AgentCanBeUsedAsSubAgentTool()
    {
        var subModel = new MockLanguageModel()
            .ReturnsToolCall("lookup_order", new { orderId = "Z9" })
            .ReturnsText("Z9 enviado");
        var orders = new OrderAgent(subModel);

        var mainModel = new MockLanguageModel()
            .ReturnsToolCall("order_agent", new { task = "Estado de Z9" })
            .ReturnsText("El pedido Z9 está enviado");
        var orchestrator = new ToolLoopAgent(new AgentSettings { Model = mainModel, Tools = [orders.AsTool()] });

        var result = await orchestrator.GenerateAsync("¿Z9?", TestContext.Current.CancellationToken);

        Assert.Equal("El pedido Z9 está enviado", result.Text);
        Assert.Equal("\"Z9 enviado\"", result.Steps[0].ToolResults[0].Output.GetRawText());
        Assert.Equal("Estado de Z9", subModel.Requests[0].Messages[^1].Text);
    }

    [Fact]
    public async Task MiddlewareTransformsRequestsAndLogs()
    {
        var logs = new List<string>();
        var inner = new MockLanguageModel().ReturnsText("ok");
        var model = inner.WithMiddleware(Middlewares.DefaultSettings(temperature: 0.2, maxOutputTokens: 100), Middlewares.Logging(logs.Add));

        await Ai.GenerateTextAsync(new GenerateTextOptions { Model = model, Prompt = "x", MaxOutputTokens = 50 });

        Assert.Equal(0.2, inner.Requests[0].Temperature);
        Assert.Equal(50, inner.Requests[0].MaxOutputTokens);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void RegistryResolvesProvidersAndAliases()
    {
        var provider = new TestProvider();
        var registry = new ProviderRegistry().Register(provider).Alias("fast", "test:small");

        Assert.Equal("big", registry.LanguageModel("test:big").ModelId);
        Assert.Equal("small", registry.LanguageModel("fast").ModelId);
        Assert.Throws<KeyNotFoundException>(() => registry.LanguageModel("nope:x"));
    }

    [Fact]
    public async Task EmbedManyBatchesAndComputesSimilarity()
    {
        var model = new MockEmbeddingModel(dimensions: 4, maxEmbeddingsPerCall: 2);

        var result = await Ai.EmbedManyAsync(model, ["a", "b", "c", "a"], TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Calls);
        Assert.Equal(4, result.Embeddings.Count);
        Assert.Equal(1.0, Ai.CosineSimilarity(result.Embeddings[0], result.Embeddings[3]), 6);
    }

    [Fact]
    public async Task DependencyInjectionRegistersRegistryAndAgents()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILanguageModel>(new MockLanguageModel().ReturnsText("hola"));
        services.AddAiSdk(ai => ai
            .AddProvider("test", _ => new TestProvider())
            .AddAlias("default", "test:big")
            .AddAgent<OrderAgent>());

        await using var sp = services.BuildServiceProvider();

        Assert.Equal("big", sp.GetRequiredService<ProviderRegistry>().LanguageModel("default").ModelId);
        var agent = sp.CreateScope().ServiceProvider.GetRequiredService<OrderAgent>();
        Assert.Equal("hola", (await agent.GenerateAsync("hi", TestContext.Current.CancellationToken)).Text);
        Assert.IsType<InMemoryConversationStore>(sp.GetRequiredService<IConversationStore>());
    }

    private sealed class TestProvider : IProvider
    {
        public string Name => "test";

        public ILanguageModel LanguageModel(string modelId) => new MockLanguageModel(modelId);
    }
}
