# Roadmap

> 🇪🇸 [Leer en español](es-Hoja-de-ruta)

## ✅ Done

- [x] Published on nuget.org with trusted publishing (OIDC, no long-lived API key) and provenance attestations:
  `0.1.0-preview.1`, `0.2.0-preview.1` (security and stability release) and `0.3.0-preview.1` (tool approval in
  `useChat`, PostgreSQL workflow store). See the [CHANGELOG](https://github.com/bluesmilecl/AiSdk.Net/blob/main/CHANGELOG.md).
- [x] Live integration tests with real API keys — **partially**: the OpenAI live tests pass (skills, router, artifacts,
  map-reduce…); PostgreSQL + pgvector integration tests run in CI.
- [x] Security review against the OWASP Top 10 for LLM applications and the Agentic Top 10: authorization on every
  endpoint, fail-closed tool approvals, signed approvals in `useChat`, skill provenance, SSRF-safe downloads, bounded
  MCP/stdio, documents and media, HMAC webhooks with timestamps.
- [x] Supply chain: pinned actions, read-only `GITHUB_TOKEN`, CodeQL, OpenSSF Scorecard, NuGetAudit as errors.
- [x] Large scanned PDFs: page-by-page OCR, one OpenMP thread per Tesseract process, OCR time budget with partial results.
- [x] `TimeProvider` in `WorkflowEngine`.
- [x] PostgreSQL `IWorkflowStore` (`AiSdk.Net.Workflows.Postgres`) for several replicas.

## 🎯 Towards 1.0

- [ ] Naming decision for packages and namespaces (`AiSdk.*` namespaces with `AiSdk.Net.*` packages, or `AiSdk.Net.*` everywhere)
- [ ] XML documentation in English
- [ ] Public API analyzers (`PublicAPI.Shipped.txt`) so the API cannot change by accident
- [x] OpenTelemetry `gen_ai` semantic conventions
- [ ] Reverse interop with Microsoft.Extensions.AI (expose AiSdk models as `IChatClient`)

## 🚧 Pending / ideas

- [ ] Live integration tests for Anthropic, Gemini and Mistral
- [ ] Medium/low pending items from `docs/quality/test-anti-patterns.md`
- [ ] Native Azure OpenAI with Entra ID authentication and Vertex AI (today: compatible endpoints or Microsoft.Extensions.AI)
- [ ] Redis store for resumable streams
- [ ] Table detection in digital PDFs without OCR (today: use `PdfOcr = PdfOcrMode.Always` with a model-based OCR)
- [ ] Generative UI and additional server tools (computer use, memory)
- [ ] `IWorkflowStore` stores for SQL Server/Redis and a web dashboard for runs
- [ ] Declarative (JSON) workflows so agents can build complete flows, not just tasks
- [ ] PowerPoint generation and charts in Excel

> [!TIP]
> Want to help with any of these? Issues and pull requests are welcome — see [Quality](Quality) for the standards the project follows.

---

**Next:** [FAQ](FAQ) ➡️
