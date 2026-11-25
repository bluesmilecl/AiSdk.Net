# Hoja de ruta

> 🇬🇧 [Read in English](Roadmap)

## ✅ Hecho

- [x] Publicado en nuget.org con trusted publishing (OIDC, sin API key de larga duración) y atestaciones de procedencia:
  `0.1.0-preview.1`, `0.2.0-preview.1` (versión de seguridad y estabilidad) y `0.3.0-preview.1` (aprobación de
  herramientas en `useChat`, almacén de workflows en PostgreSQL). Ver el [CHANGELOG](https://github.com/bluesmilecl/AiSdk.Net/blob/main/CHANGELOG.md).
- [x] Pruebas de integración en vivo con API keys reales — **parcialmente**: las de OpenAI pasan (skills, router,
  artifacts, map-reduce…); las de PostgreSQL + pgvector corren en CI.
- [x] Revisión de seguridad según el OWASP Top 10 para aplicaciones LLM y el Agentic Top 10: autorización en todos los
  endpoints, aprobaciones que fallan cerradas, aprobaciones firmadas en `useChat`, procedencia de las skills, descargas
  sin SSRF, límites en MCP/stdio, documentos y multimedia, webhooks HMAC con marca de tiempo.
- [x] Cadena de suministro: actions fijadas por SHA, `GITHUB_TOKEN` de solo lectura, CodeQL, OpenSSF Scorecard, NuGetAudit como error.
- [x] PDF escaneados grandes: OCR página por página, un hilo de OpenMP por proceso de Tesseract, presupuesto de tiempo con resultado parcial.
- [x] `TimeProvider` en `WorkflowEngine`.
- [x] `IWorkflowStore` en PostgreSQL (`AiSdk.Net.Workflows.Postgres`) para varias réplicas.

## 🎯 Hacia la 1.0

- [ ] Decidir el nombre de paquetes y namespaces (namespaces `AiSdk.*` con paquetes `AiSdk.Net.*`, o `AiSdk.Net.*` en todo)
- [ ] Documentación XML en inglés
- [ ] Analizadores de API pública (`PublicAPI.Shipped.txt`) para que la API no cambie por accidente
- [ ] Convenciones semánticas `gen_ai` de OpenTelemetry
- [ ] Interoperabilidad inversa con Microsoft.Extensions.AI (exponer los modelos de AiSdk como `IChatClient`)

## 🚧 Pendiente / ideas

- [ ] Pruebas de integración en vivo para Anthropic, Gemini y Mistral
- [ ] Pendientes medios/bajos de `docs/quality/test-anti-patterns.es.md`
- [ ] Azure OpenAI con autenticación Entra ID y Vertex AI nativos (hoy: endpoints compatibles o Microsoft.Extensions.AI)
- [ ] Almacén Redis para streams reanudables
- [ ] Detección de tablas en PDF digitales sin OCR (hoy: usar `PdfOcr = PdfOcrMode.Always` con un OCR de modelo)
- [ ] Generative UI y herramientas de servidor adicionales (computer use, memoria)
- [ ] Almacenes `IWorkflowStore` para SQL Server/Redis y panel web de ejecuciones
- [ ] Workflows declarativos (JSON) para que los agentes armen flujos completos, no solo tareas
- [ ] Generación de PowerPoint y gráficos en Excel

> [!TIP]
> ¿Quieres ayudar con alguno de estos puntos? Los issues y pull requests son bienvenidos; consulta [Calidad](es-Calidad) para ver los estándares que sigue el proyecto.

---

**Siguiente:** [Preguntas frecuentes](es-Preguntas-frecuentes) ➡️
