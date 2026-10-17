# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- First public release candidate of **AiSdk.Net** for .NET 10.
- Core API: `GenerateTextAsync`, `StreamText`, `GenerateObjectAsync<T>`, `StreamObject<T>`, tools, `ToolLoopAgent`,
  class-based agents, human approval, memory, middleware, provider registry, embeddings and OpenTelemetry.
- Providers: OpenAI (and compatible servers), Anthropic Claude, Google Gemini, Mistral, Amazon Bedrock, Cohere and
  Microsoft.Extensions.AI.
- Multi-agent orchestration: sequential, parallel, router, supervisor, handoffs and evaluator-optimizer.
- Skills (open Agent Skills standard): loading, validation, progressive disclosure, slash commands, scripts, authoring
  and Anthropic container Skills.
- Artifacts, Word/PDF/Excel generation, documents to Markdown with OCR, context compression.
- Durable workflows, triggers and automations (`AiSdk.Net.Workflows`).
- RAG: vector store, filters, hybrid search, reranking, PostgreSQL + pgvector and Microsoft.Extensions.VectorData.
- MCP client (tools, resources, prompts, roots, sampling, elicitation).
- ASP.NET Core backend for AI SDK UI `useChat`, resumable streams, artifacts/workflows/skills endpoints.
- Open-source setup: bilingual README and wiki (English/Spanish), contributing guide, code of conduct, security
  policy, issue and PR templates.

### Changed
- Reasoning-model friendly defaults: internal calls (OCR, compression, orchestration) no longer force `Temperature = 0`.
