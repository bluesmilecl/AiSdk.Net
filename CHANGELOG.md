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

- `IExternalProcessRunner`, `ExternalProcessRunner` and `ExternalProcess.Runner`: external tools (tesseract, pdftoppm,
  ffmpeg) can run through a configurable wrapper and with an allowlisted environment. `TesseractOcr`,
  `PdftoppmRasterizer` and `MarkdownConverterOptions.ProcessRunner` accept their own runner.
- `PgVectorStoreOptions.FilteredSearch` (`Auto`, `Approximate`, `Exact`).
- `MarkdownConverterOptions.Progress` (`DocumentConversionProgress`), `DocumentSource.FilePath` and
  `IPdfRasterizer.RenderFileAsync`.

### Changed
- Reasoning-model friendly defaults: internal calls (OCR, compression, orchestration) no longer force `Temperature = 0`.
- PgVector filters: equalities are translated to jsonb containment (`@>`), served by the GIN metadata index; `In` uses
  `@> ANY(...)` and `Exists` the `?` operator. Metadata is compared as JSON strings (what `UpsertAsync` stores). With
  `FilteredSearch = Auto`, a selective filter no longer leaves a search with fewer results than requested.
- PDF OCR runs page by page: each scanned page is rasterized and recognized on its own (bounded by `MaxConcurrency`),
  embedded images are read only for pages that need OCR and could not be rasterized, and a page that fails to rasterize
  falls back to its embedded image instead of failing the whole document.
- `PdftoppmRasterizer` reads the PDF from a file without copying it, renders consecutive pages with one process and
  ranges in parallel, and skips pages that fail.
