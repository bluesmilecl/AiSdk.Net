# Changelog

All notable changes to this project are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-26

First stable release. The public API is frozen and tracked with public API analyzers, and the project follows
Semantic Versioning from now on: no breaking changes until 2.0 (see
[Versioning and compatibility](https://github.com/bluesmilecl/AiSdk.Net/wiki/Quality#versioning-and-compatibility)).
This release also brings English documentation and messages (with Spanish by culture), OpenTelemetry GenAI telemetry
and the reverse Microsoft.Extensions.AI interop. Upgrading from 0.3: read **Changed**; every breaking change has a
one-line migration note.

### Added
- **Public API tracking:** every package ships `PublicAPI.Shipped.txt` (5,010 members) checked by
  `Microsoft.CodeAnalysis.PublicApiAnalyzers`; an unannounced public change does not compile.
- **Localized messages:** exceptions, warnings, HTTP problem details and the texts the model receives (tool denials,
  approval errors, prompts of orchestration, context compression, skills, artifacts, OCR and document tools) come from
  `.resx` resources: English by default, Spanish when `CultureInfo.CurrentUICulture` is `es*` (satellite assemblies
  inside the packages). Spanish texts are unchanged from 0.3.
- `LocalizedDescriptionAttribute`: a `DescriptionAttribute` read from resources with the current UI culture, used for
  the SDK's own tool and schema descriptions and available for yours.
- `VisionModelOcr.IsNoTextReply`: recognizes the "no text" answer in English and Spanish.
- **Reverse interop with Microsoft.Extensions.AI** in `AiSdk.Net.Extensions.AI`: `model.AsIChatClient()` exposes any
  `ILanguageModel` as an `IChatClient` (`LanguageModelChatClient`) and `embeddingModel.AsIEmbeddingGenerator()` exposes
  any `IEmbeddingModel` as an `IEmbeddingGenerator<string, Embedding<float>>` (`EmbeddingModelGenerator`, batched by
  `MaxEmbeddingsPerCall`, with usage). Semantic Kernel, Microsoft Agent Framework, `FunctionInvokingChatClient`,
  OpenTelemetry and caching middleware, Aspire and evaluation libraries can now use any AiSdk.Net provider.
  `GetResponseAsync` and `GetStreamingResponseAsync` map text, reasoning, images, files, function calls and results;
  `ChatOptions` (instructions, temperature, top-p/top-k, max output tokens, penalties, stop sequences, seed, JSON and
  JSON schema response formats, so `GetResponseAsync<T>()` works, function declarations and `ToolMode`); usage
  (including reasoning and cached tokens), finish reason, response id and model id. The client never executes tools:
  calls come back as `FunctionCallContent` (provider-executed ones as `InformationalOnly`). `RawRepresentationFactory`
  can return `ProviderOptions` or a `LanguageModelRequest`; `GetService` returns `ChatClientMetadata` /
  `EmbeddingGeneratorMetadata` (with an optional endpoint) and the underlying model. Reasoning signatures and provider
  metadata survive streaming and conversation histories saved as JSON.
- Converting back unwraps instead of wrapping twice (`model.AsIChatClient().AsLanguageModel()` returns `model`, and the
  same for embeddings). Through an MEAI pipeline (`.UseOpenTelemetry()`, ...) provider options, headers and
  provider-defined tools still reach the AiSdk.Net model.
- **OpenTelemetry GenAI metrics** on a `Meter` named `"AiSdk"` (`AiTelemetry.MeterName`, register it with
  `AddMeter(AiTelemetry.MeterName)`): `gen_ai.client.operation.duration` (histogram, `s`) and `gen_ai.client.token.usage`
  (histogram, `{token}`, `gen_ai.token.type` = `input`/`output`) for every `chat` and `embeddings` call, with
  `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.request.model`, `gen_ai.response.model`, `error.type` on
  failures and the bucket boundaries the conventions recommend.
- New spans: `embeddings {model}` for every embeddings call (also batches and vector stores), `invoke_agent {agent}` for
  agent runs (with `gen_ai.agent.name`, `gen_ai.agent.description` and, in `AgentSession`, `gen_ai.conversation.id`), and
  `chat {model}` spans in streaming too (with `gen_ai.request.stream` and `gen_ai.response.time_to_first_chunk`).

### Changed
- **XML documentation in English** (IntelliSense and nuget.org) in all packages.
- **Breaking: messages are English by default.** Code or tests that compare SDK message texts in Spanish must set a
  Spanish UI culture (for example `CultureInfo.DefaultThreadCurrentUICulture = new("es-CL")` or
  `app.UseRequestLocalization(...)`) or compare the English text. Message texts are not part of the API contract.
- **Breaking: localized instructions are properties instead of constants:** `SupervisorOrchestration.DefaultInstructions`,
  `VisionModelOcr.DefaultInstructions`, `ArtifactTools.Instructions`, `SkillAuthoringTools.Instructions`,
  `SkillGenerator.AuthoringGuide`, `DocumentTools.Instructions`, `VideoConverter.FramePrompt` and
  `AgentTasks.Instructions`. Migration: recompile; they can no longer be used where a constant is required.
- **Breaking:** `AiTelemetry` moved from `AiSdk.Internal` to `AiSdk`. Migration: `using AiSdk;`.
- **Breaking:** `McpRequestRejectedException(string? message = null)` (null uses the localized text),
  `VectorStoreExtensions.AsTool(..., string? description = null, ...)` (null uses the localized description) and
  `DocumentGenerator.ToXlsx<T>(..., string? sheetName = null)` ("Data" or "Datos" by culture). Migration: recompile.
- **Breaking: telemetry follows the OpenTelemetry semantic conventions for generative AI** (span names and attributes of
  semantic-conventions v1.41.0 and semantic-conventions-genai v0.1.0). The `ai.*` spans and attributes are gone:

  | Before (0.x) | Now |
  | --- | --- |
  | span `ai.generateText` / `ai.streamText` | `invoke_agent {gen_ai.agent.name}` when an agent runs the loop, otherwise `invoke_generate_text` / `invoke_stream_text` (`invoke_generate_object` / `invoke_stream_object` for objects) |
  | span `ai.generateText.step` | `chat {gen_ai.request.model}`, kind `Client`, one per model call (retries included), now also in streaming |
  | span `ai.toolCall` | `execute_tool {gen_ai.tool.name}` |
  | `ai.model.provider` | `gen_ai.provider.name`, with the well-known values (`google` → `gcp.gemini`, `bedrock` → `aws.bedrock`; other ids unchanged) |
  | `ai.model.id` | `gen_ai.request.model` |
  | `ai.response.finishReason` (`Stop`) | `gen_ai.response.finish_reasons` (`["stop"]`) |
  | `ai.usage.inputTokens` / `ai.usage.outputTokens` | `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` |
  | `ai.toolCall.name` / `ai.toolCall.id` | `gen_ai.tool.name` / `gen_ai.tool.call.id` |

  Failed spans now also carry `error.type` (HTTP status code of provider errors, otherwise the exception type), and a
  cancelled run is reported as failed. Message content is still never recorded. **Migration:** keep
  `AddSource(AiTelemetry.SourceName)` and update the queries, dashboards and alerts that use the old names with the table
  above.
- The `ActivitySource` version is the package version instead of a fixed `0.1.0`.

### Fixed
- `IChatClient.AsLanguageModel()`: the `ProtectedData` of reasoning content (signatures, encrypted reasoning) is sent
  back on the next turn; reasoning and cached token counts, image and file outputs and the `"error"` finish reason are
  mapped; calls marked `InformationalOnly` (and, without streaming, calls whose result is already in the response) are
  not run again by the AiSdk.Net tool loop; tool calls whose input is not a JSON object no longer throw.

## [0.3.0-preview.1] - 2026-11-25

Tool approval in `useChat` with the AI SDK's own protocol and signed approvals, a PostgreSQL workflow store for several
replicas (new package `AiSdk.Net.Workflows.Postgres`), and more hardening: MCP stdio limits, image and audio limits
before OCR and transcription, skills that cannot be written through hard links, and workflow events that only resume
the runs that were authorized. Two small breaking changes in the UI stream, with migration notes under **Changed**.

### Added
- **Tool approval with `useChat`**, using the AI SDK's own protocol (`ai@6` and `ai@7`): a call left pending by
  `DeferToolApprovals` is streamed as `tool-approval-request` (`approvalId`, `toolCallId`), so `useChat` shows the part
  in state `approval-requested`; the browser answers with `addToolApprovalResponse` and sends the conversation back
  (`sendAutomaticallyWhen: lastAssistantMessageIsCompleteWithApprovalResponses`). `UIMessageConverter.ToModelInput`
  returns the messages plus the verified `ToolApprovals`: approved calls run with the input the model produced, denied
  ones stream `tool-output-denied` and the model gets the denial with the user's reason. `output-denied` parts in the
  history reach the model as denials.
- `ToolApprovalProtector`: the `approvalId` is an opaque token that binds the approval to the tool call id, tool name
  and a SHA-256 of the canonical JSON input, to a scope (user and chat) and to an expiry (1 hour by default). Forged ids,
  another user's or chat's approvals, expired ones, and changed inputs or call ids are rejected (the tool does not run
  and the model gets an error). `ToolApprovalProtection.ForChat(http, chatId)` in ASP.NET Core (Data Protection, no
  secret to configure); `ToolApprovalProtector.CreateHmac(key, scope)` elsewhere.
- `toolApprovals` parameter on `ToUIMessageStreamResult`, `ToResumableUIMessageStreamResult`,
  `WriteUIMessageStreamAsync`, `UIMessageStream.ToChunks`/`WriteAsync` and `UIMessageStreamWriter.Merge`;
  `AgentCallOptions.DeferToolApprovals`; stream parts `ToolApprovalRequestStreamPart` and `ToolOutputDeniedStreamPart`.
- `samples/ChatApi`: `send_weather_alert` requires the user's approval in the chat.
- `WorkflowEngine.SendEventToRunsAsync`: delivers an event only to the given runs (still re-checked under the run lock),
  and fires matching `EventTrigger` automations like `SendEventAsync`.
- New package `AiSdk.Net.Workflows.Postgres`: `PostgresWorkflowStore`, an `IWorkflowStore` on PostgreSQL so durable
  workflows run on several machines against one database. Optimistic concurrency in SQL (`UPDATE ... WHERE version`,
  `INSERT ... ON CONFLICT DO NOTHING`), partial indexes for the engine queries, batched retention with `SKIP LOCKED`,
  idempotent schema creation safe across replicas (`EnsureSchemaAsync`, configurable schema and table prefix) and
  `UsePostgresStore()` for `AddAiSdkWorkflows`.

### Changed
- Denied calls are emitted as `ToolOutputDeniedStreamPart` instead of an error `ToolResultStreamPart` (the model still
  receives the same error result). Migration: read `ToolOutputDeniedStreamPart.ToolResult` where you handled denials.
- A call that is pending without a decision when resuming now reads "no hubo una decisión válida"; pending calls of
  tools that no longer exist are closed with an error instead of being left without a result; a repeated call id is
  executed once.
- `UIMessageStream.WriteAsync` takes `toolApprovals` before `cancellationToken`. Migration: pass the token by name
  (`cancellationToken: ct`).
- Workflows: `FileSystemWorkflowStore` honors a `limit` of 0 or less like the other stores (no runs listed, none
  deleted); it used to return or delete one run.
- MCP: roots and sampling (`Roots`, `SetRootsAsync`, `SamplingModel`, `SamplingHandler`, `ApproveSampling`,
  `ReviewSamplingResult`, `MaxSamplingTokens` and their types) are marked `[Experimental("AISDKMCP001")]`: the MCP
  specification 2026-07-28 deprecates them. Suppress the diagnostic to keep using them.
- Audio: with ffmpeg installed, audio longer than `MaxMediaDuration` is trimmed before transcription, with a warning.

### Security
- Approved calls are never repaired by `RepairToolCall`: repairing would execute something other than what the user
  approved, so an approved call with invalid input fails instead.
- Without a `ToolApprovalProtector`, a call waiting for approval is streamed as `tool-output-error` and approval answers
  are ignored (fails closed); `ToModelMessages` drops `approval-responded` parts.
- When resuming approvals, two different calls with the same tool call id in the last assistant message (a tampered
  history reusing an approved call's id for another tool or input) are both closed with an error: an approval is never
  applied to a call other than the one it was granted for.
- `UIMessageStreamWriter.WriteSource` no longer writes `"title": null` without a title (ai 6.0.x rejects it).
- MCP over stdio: lines longer than `StdioMcpTransport.MaxMessageBytes` (16 MiB) are discarded without buffering them,
  stderr lines are cut at 64 KiB (`MaxStderrLineBytes`) and the incoming message queue is bounded.
- Documents: an image whose header declares more than `MaxRasterPixels` × `MaxRasterPixels` pixels is never sent to OCR
  (PNG, JPEG, GIF, BMP and WebP headers are read without decoding), with a warning.
- Google: model ids are escaped segment by segment and `.`/`..` segments are rejected.
- Skills: `SkillLibrary.SaveAsync` writes each file to a hidden temporary file and renames it over the target, so a
  hard link, FIFO or symlink left in the skill folder is replaced instead of written through (a hard link to a file
  outside the skill is indistinguishable from a regular file). Empty files and FIFOs are never opened when loading or
  reading a skill folder.

- Workflows: `POST /events/{event}` only resumes the runs `CanSendEvent` approved. A run that started waiting for the
  same event between the check and the delivery used to be resumed too; it now keeps waiting.

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
