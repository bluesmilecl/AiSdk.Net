# Roadmap

> 🇪🇸 [Leer en español](es-Hoja-de-ruta)

## ✅ Done

- [x] Run the integration tests with real API keys (secrets configured in GitHub) — **partially**:
  the OpenAI live tests pass (11 tests, including skills, router, artifacts and map-reduce).
- [x] Release pipeline ready with trusted publishing (OIDC, no long-lived API key) — see
  `docs/quality/nuget-trusted-publishing.md`; releases are triggered by pushing a `vX.Y.Z` tag (starting with `v0.1.0`)

## 🚧 Pending / ideas

- [ ] First release to nuget.org (`v0.1.0`)
- [ ] Live integration tests for Anthropic, Gemini and Mistral
- [ ] `TimeProvider` in `WorkflowEngine` and the medium/low pending items from `docs/quality/test-anti-patterns.md`
- [ ] Native Azure OpenAI with Entra ID authentication and Vertex AI (today: compatible endpoints or Microsoft.Extensions.AI)
- [ ] Redis store for resumable streams
- [ ] Table detection in digital PDFs without OCR (today: use `PdfOcr = PdfOcrMode.Always` with a model-based OCR)
- [ ] Generative UI and additional server tools (computer use, memory)
- [ ] `IWorkflowStore` stores for SQL Server/PostgreSQL/Redis and a web dashboard for runs
- [ ] Declarative (JSON) workflows so agents can build complete flows, not just tasks
- [ ] PowerPoint generation and charts in Excel

> [!TIP]
> Want to help with any of these? Issues and pull requests are welcome — see [Quality](Quality) for the standards the project follows.

---

**Next:** [FAQ](FAQ) ➡️
