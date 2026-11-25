# AiSdk.Net

**The AI and agents SDK for .NET 10.** One provider-agnostic API for text, streaming, typed objects, tools,
agents, multi-agent orchestration, RAG, Skills, durable workflows, MCP and more. Inspired by the Vercel AI SDK
and the Laravel AI SDK. Open source (MIT), created by Bluesmile ([@bluesmilecl](https://github.com/bluesmilecl)) and Esteban Cortes
([@ecr17dev](https://github.com/ecr17dev)).

## Install

AiSdk.Net is in preview: install it with `--prerelease` (the API may change before the first stable version).

```bash
dotnet add package AiSdk.Net.OpenAI --prerelease   # or AiSdk.Net.Anthropic, .Google, .Mistral, .Bedrock, .Cohere
```

## Quick start

```csharp
using System.ComponentModel;
using AiSdk;
using AiSdk.OpenAI;

var weather = Tool.FromDelegate("weather", "Gets the current weather for a city",
    ([Description("City name")] string city) => new { city, temperatureC = 14, rain = "80%" });

var agent = new ToolLoopAgent(new AgentSettings
{
    Model = OpenAIProvider.Default.Responses("gpt-5"),   // reads OPENAI_API_KEY
    Instructions = "You are a friendly weather assistant.",
    Tools = [weather],
});

await foreach (var token in agent.Stream("Should I take an umbrella in Santiago?").TextStream)
{
    Console.Write(token);
}
```

## Packages

| Package | What it does |
|---|---|
| `AiSdk.Net` | Core: `Ai.*` API, tools, agents, orchestration, artifacts, skills, middleware, embeddings, `MockLanguageModel` |
| `AiSdk.Net.OpenAI` · `.Anthropic` · `.Google` · `.Mistral` · `.Bedrock` · `.Cohere` | Native providers |
| `AiSdk.Net.Mcp` | Model Context Protocol client |
| `AiSdk.Net.Extensions.AI` | Microsoft.Extensions.AI adapter in both directions: `IChatClient` as an AiSdk model and AiSdk models as `IChatClient` / `IEmbeddingGenerator` |
| `AiSdk.Net.Extensions.DependencyInjection` | `services.AddAiSdk(...)` |
| `AiSdk.Net.AspNetCore` | Backend for AI SDK UI `useChat`, artifacts, workflows and skills endpoints |
| `AiSdk.Net.Documents` | Documents to Markdown with page-by-page OCR; Word, PDF and Excel generation |
| `AiSdk.Net.Workflows` · `AiSdk.Net.Workflows.Postgres` | Durable workflows, triggers and automations; PostgreSQL store for several replicas |
| `AiSdk.Net.PgVector` · `AiSdk.Net.VectorData` | Vector stores (pgvector, Qdrant, Pinecone, Azure AI Search…) |

## Learn more

- Repository and visual README: https://github.com/bluesmilecl/AiSdk.Net
- Documentation (English and Spanish): https://github.com/bluesmilecl/AiSdk.Net/wiki
- Issues: https://github.com/bluesmilecl/AiSdk.Net/issues
