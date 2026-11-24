# ❓ FAQ

> 🇪🇸 [Leer en español](es-Preguntas-frecuentes)

<details open>
<summary><b>How is AiSdk.Net different from Microsoft.Extensions.AI or Semantic Kernel?</b></summary>

Microsoft.Extensions.AI gives you low-level abstractions (`IChatClient`); AiSdk.Net builds a complete,
opinionated toolkit on top of a similar abstraction: agent loops, multi-agent orchestration, Skills, artifacts,
durable workflows, RAG, document conversion and a `useChat` backend. They work together: any `IChatClient` can be
used as an AiSdk.Net model with `AsLanguageModel()` (see [ASP.NET Core and useChat](ASP.NET-Core-and-useChat)).
If you know the Vercel AI SDK or Laravel AI, the API will feel familiar ([Feature map](Feature-Map)).
</details>

<details>
<summary><b>Which providers are supported?</b></summary>

Native: OpenAI, Anthropic Claude, Google Gemini, Mistral, Amazon Bedrock and Cohere. Any OpenAI-compatible server
(Ollama, Groq, DeepSeek, OpenRouter, LM Studio, vLLM…) works through `openai.Chat(...)`, and anything with a
Microsoft.Extensions.AI client works through the adapter. See [Providers](Providers).
</details>

<details>
<summary><b>Can I try it without an API key?</b></summary>

Yes. `MockLanguageModel` scripts responses and tool calls, and every sample runs with it:
`dotnet run --project samples/QuickStart -- mock agent`.
</details>

<details>
<summary><b>Can I run local models?</b></summary>

Yes, with Ollama, LM Studio or vLLM through the OpenAI-compatible provider. OCR can also run locally with Tesseract
(see [Documents, OCR and context](Documents-OCR-and-Context)).
</details>

<details>
<summary><b>Why are the packages called <code>AiSdk.Net.*</code> but the namespaces <code>AiSdk.*</code>?</b></summary>

The `AiSdk` id was already taken on nuget.org, so the packages use the `AiSdk.Net` prefix. The code keeps short
namespaces: `using AiSdk;`, `using AiSdk.OpenAI;`.
</details>

<details>
<summary><b>Is it production ready?</b></summary>

The project is at version 0.x: the API is complete and tested (200+ unit tests and live tests), but it may still
change before 1.0. Pin versions and check the [Roadmap](Roadmap) and release notes in `CHANGELOG.md`.
</details>

<details>
<summary><b>Does the SDK send telemetry anywhere?</b></summary>

No. It only emits `Activity` spans on the `"AiSdk"` source; they go nowhere unless your app configures
OpenTelemetry to export them (see [Testing and telemetry](Testing-and-Telemetry)).
</details>

<details>
<summary><b>Are third-party Skills safe?</b></summary>

Skills loaded from GitHub or zip files are untrusted by default: the agent can read their instructions but cannot
run their scripts unless you mark them trusted or opt in explicitly. Paths are validated against traversal. See
[Skills](Skills).
</details>

<details>
<summary><b>How can I contribute?</b></summary>

Read [CONTRIBUTING.md](https://github.com/bluesmilecl/AiSdk.Net/blob/HEAD/CONTRIBUTING.md), pick a
[`good first issue`](https://github.com/bluesmilecl/AiSdk.Net/labels/good%20first%20issue) or improve these
docs in `docs/wiki`. Questions are welcome in [Discussions](https://github.com/bluesmilecl/AiSdk.Net/discussions).
</details>

<details>
<summary><b>Who created AiSdk.Net and what is the license?</b></summary>

It was created by **Bluesmile** ([@bluesmilecl](https://github.com/bluesmilecl)) and **Esteban Cortes**
([@ecr17dev](https://github.com/ecr17dev)) and is released under the
[MIT license](https://github.com/bluesmilecl/AiSdk.Net/blob/HEAD/LICENSE).
</details>

---

⬅️ Back to [Home](Home)
