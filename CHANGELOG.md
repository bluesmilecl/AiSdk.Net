# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Security
- MCP over stdio: lines longer than `StdioMcpTransport.MaxMessageBytes` (16 MiB) are discarded without buffering them,
  stderr lines are cut at 64 KiB (`MaxStderrLineBytes`) and the incoming message queue is bounded.
- Documents: an image whose header declares more than `MaxRasterPixels` × `MaxRasterPixels` pixels is never sent to OCR
  (PNG, JPEG, GIF, BMP and WebP headers are read without decoding), with a warning.
- Google: model ids are escaped segment by segment and `.`/`..` segments are rejected.

### Changed
- MCP: roots and sampling (`Roots`, `SetRootsAsync`, `SamplingModel`, `SamplingHandler`, `ApproveSampling`,
  `ReviewSamplingResult`, `MaxSamplingTokens` and their types) are marked `[Experimental("AISDKMCP001")]`: the MCP
  specification 2026-07-28 deprecates them. Suppress the diagnostic to keep using them.
- Audio: with ffmpeg installed, audio longer than `MaxMediaDuration` is trimmed before transcription, with a warning.

## [0.2.0-preview.1] - 2026-11-25

Security and stability release. It follows a full review of the SDK against the OWASP Top 10 for LLM Applications
(2026), the OWASP Top 10 for Agentic Applications and the MCP security best practices. Several **defaults change to
fail closed**; every breaking change below has a one-line migration note.

### Security
- **ASP.NET Core endpoints are no longer open by default.** `MapArtifacts`, `MapWorkflows` and `MapSkills` require
  authorization unless `AllowAnonymous = true` (`EndpointSecurityOptions`: `AuthorizationPolicies`, `ResolveUser`).
- **Artifacts:** per-owner access (`Artifact.Owner`, `ArtifactEndpointOptions.CanAccess`); no global listing; listings
  are metadata-only; artifact tools fail without a conversation or user in `Context` (`AllowUnscoped` to opt out).
  `/raw` and file responses are sent with a sandbox CSP and `nosniff`, and active content (HTML, SVG, XML, JS) is
  served as a download, so model-written HTML never runs on the app's origin (stored XSS).
- **Workflows over HTTP:** per-run and per-automation access callbacks (`CanStart`, `CanAccessRun`,
  `CanAccessAutomation`, `CanSendEvent`; events are denied until `CanSendEvent` is set), owners resolved on the server,
  `application/json` required on every POST (cross-site form CSRF), 1 MiB body limit (`MaxRequestBodyBytes`, 413) and
  webhook secrets redacted from listings.
- **Webhooks:** a secret of at least 16 characters is required (`AllowUnauthenticated` to opt out); the default scheme
  signs `"{timestamp}.{body}"` (`X-Signature-256` + `X-Webhook-Timestamp`, 5-minute tolerance), replays return the same
  run and `X-Webhook-Id` deduplicates retries; automation ids carry 128 random bits.
- **Chat (useChat):** system messages sent by the browser are dropped (`UIMessageConverterOptions.AllowSystemMessages`);
  `ServerExecutedTools` drops forged results of server-side tools; tool error text in the UI stream goes through
  `onError` and is masked by default (the model still sees it); resumable streams are scoped to their owner; malformed
  parts are skipped instead of returning 500.
- **Skills:** skills created by an agent keep a provenance marker (`.aisdk-provenance.json`) and load as untrusted even
  from a trusted folder; `create_skill` rejects scripts unless `AllowScripts`; `SkillBuilder.Build()` is untrusted by
  default; `run_skill_script` requires approval by default (`RequireScriptApproval`); symlinks, junctions and hidden
  files are never read, listed or followed; the script runner always kills the process tree and bounds output and
  created files; a later load no longer silently replaces a same-named skill; zip and GitHub downloads are size-capped.
- **Tool approvals fail closed:** without `OnToolApproval` a tool that needs approval returns an explicit error instead
  of leaving a dangling call. New approve-later flow: `DeferToolApprovals` + `PendingApprovals`, resumed with
  `GenerateTextOptions.ToolApprovals` or `AgentSession.SendApprovalsAsync`; sessions repair histories left dangling by
  0.1.0; sub-agents use the parent's approval handler.
- **MCP client:** stdio servers get a minimal environment instead of all the host's secrets (`InheritedVariables`,
  `InheritEnvironment = true` to opt out); sampling requires `ApproveSampling` (human in the loop) and is capped
  (`MaxSamplingTokens` 4096, `MaxConcurrentServerRequests` 4); servers only receive generic errors
  (`ServerRequestFailed` event for details); `RequireToolApproval`, `ToolNamePrefix` and name-collision checks; tool
  definitions are pinned by SHA-256 and a changed definition is blocked (`ToolChangePolicy`, "rug pull").
- **Providers:** `OPENAI_API_KEY` is only read for `https://api.openai.com`; `OpenAIProvider.Compatible()` never sends it
  to third-party servers. Authenticated requests don't follow POST or HTTPS→HTTP redirects and strip auth headers
  cross-origin (`SafeRedirectHandler`); ids are escaped in URL paths; Bedrock rejects event-stream messages over 16 MB.
- **Documents:**
  - URL downloads are SSRF-safe (`UrlDownloader`/`UrlDownloadOptions`): http(s) only, private, loopback, link-local and
    metadata addresses blocked at connect time, redirects re-validated, size and time limits.
  - Hostile files can no longer crash the process or exhaust memory: bounded HTML depth and size (no stack overflow,
    no quadratic regexes), Office part depth and size, DOCX spans and list levels, XLSX columns and rows, CSV columns.
  - External tools (tesseract, pdftoppm, ffmpeg) get only `CommonEnvironmentVariables`, a timeout and output caps, and
    ffmpeg runs with protocol and format whitelists (no remote fetches from playlists) and a duration cap; temp
    directories are private (0700).
  - Spreadsheet formulas are off by default (`SpreadsheetBuilder.AllowFormulas`); the agent's `create_spreadsheet`
    keeps calculation formulas but filters network, link and DDE functions (`FormulaFilter`).
  - Generated DOCX/PDF links are limited to http, https and mailto; the attachment wrapper given to the model is
    escaped.
- **Supply chain:** GitHub Actions pinned by commit SHA, read-only `GITHUB_TOKEN` by default, provider keys only on the
  default branch, release split into build and publish jobs with SLSA provenance attestations for every `.nupkg`,
  CodeQL and OpenSSF Scorecard workflows, NuGetAudit on transitive packages with high/critical advisories failing the
  build.

### Stability
- **OCR of large PDFs:** external tools run with `OMP_THREAD_LIMIT=1` unless it is set (`LimitOpenMpThreads`).
  Tesseract's OpenMP threads made parallel pages crawl (4 pages took over 10 minutes on 4 cores instead of 2 seconds);
  a 400-page, 212 MB scanned PDF is now read in about 4 minutes on 4 cores. Fully blank pages (no text, images or
  drawings) are never sent to OCR. New `MaxOcrDuration`: when it runs out, the pages read so far are returned with a
  warning instead of the whole conversion being cancelled.
- Limits for documents: `MaxInputBytes` (512 MB), `MaxNestingDepth` (128), `MaxHtmlBytes` (10 MB),
  `MaxOfficePartChars` (32M), `MaxPdfPages` (2000), `MaxOcrPages` (1000, also covers embedded images; lower it with a
  paid vision model), `MaxRasterPixels` (5000 px long side), `MaxMediaDuration` (1 h); external processes 10 min,
  64 MB stdout.
- Workflows: per-automation error isolation with backoff and auto-disable (`MaxAutomationFailures`), schedules and time
  zones validated at creation, cancellation signals the running workflow and is final, optimistic concurrency
  (`Version`) and run leases so replicas sharing a store don't duplicate work, indexed store queries instead of full
  listings, retention (`RunRetention` 30 days, `PurgeRunsAsync`), bounded in-memory store.
- MCP over HTTP: `RequestTimeout` covers the whole response, `MaxMessageBytes` (16 MiB), a broken SSE stream fails the
  pending request at once, `DisposeAsync` never throws, pagination stops on repeated cursors and after `MaxListPages`.
- Agent loop: `MaxParallelToolCalls` (8); linear partial-JSON repair for streamed objects (was quadratic); provider
  alias cycles fail with a clear error; tool-call repair honours cancellation.
- HTTP: SSE line and event caps, optional idle timeout for model streams (10 min), keep-alives and a truncated final
  event tolerated; `Retry-After` never negative and honoured up to `MaxRetryAfter` (60 s); pooled connections renewed
  every 5 minutes.

### Breaking changes (migration)
- Endpoints require authorization → configure authentication or set `AllowAnonymous = true` in the endpoint options.
- Artifacts are visible to their owner only and artifact tools need a scope → pass `Context["conversationId"]` or
  `Context["userId"]`, backfill `Owner` for old artifacts or set `CanAccess`; `ListAsync` may return `Data = null`.
- Workflow POSTs need `Content-Type: application/json`; events return 403 until `CanSendEvent` is set; `TriggeredBy` for
  HTTP-started runs is `"user:{id}"`.
- Webhooks need a secret (≥ 16 chars) and timestamped signatures → senders sign `"{ts}.{body}"`; old senders can use
  `Verification = BodySignature` or `SharedSecret`.
- `IWorkflowStore` gained queries, versions and leases (`SaveAutomationAsync` returns the saved automation) → custom
  stores must implement them; invalid automations throw at `AddAutomationAsync`.
- Client system messages are dropped (`AllowSystemMessages`); tool errors in the UI are masked (`onError`); resumable
  streams need an authenticated user or an explicit `owner`.
- `SkillBuilder.Build()` is untrusted → `Build(trusted: true)` for your own skills; `run_skill_script` needs approval →
  `OnToolApproval` or `RequireScriptApproval = false`; `Load*` keeps the first skill → `ReplaceExisting = true`.
- Approval without a handler returns an error result → set `OnToolApproval` or use `DeferToolApprovals`.
- `MaxParallelToolCalls` defaults to 8; a `Retry-After` above 60 s surfaces an `ApiCallException`; a malformed stream
  event throws `AiSdkException`.
- `OpenAIProviderOptions.ApiKeyEnvironmentVariable` is nullable and a non-OpenAI `BaseUrl` without a key throws → pass
  `apiKey` or `apiKeyEnvironmentVariable` to `Compatible()`.
- MCP: stdio servers don't inherit the environment; a bare `SamplingModel` throws (add `ApproveSampling`); changed tool
  definitions are blocked; `GetToolsAsync` has a new third parameter (pass `cancellationToken:` by name).
- Documents: `MarkdownConverterOptions.HttpClient` is replaced by `UrlDownload`; external tools don't inherit the
  environment and time out; spreadsheet formulas are off (`AllowFormulas = true` for trusted data); oversized input
  throws `DocumentTooLargeException`; `DocumentSource.FromStreamAsync` gained `maxBytes` (pass the token by name).

## [0.1.0-preview.1] - 2026-11-24

First preview published on nuget.org.

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
