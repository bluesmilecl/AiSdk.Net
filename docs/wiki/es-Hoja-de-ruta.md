# Hoja de ruta

> 🇬🇧 [Read in English](Roadmap)

## ✅ Hecho

- [x] Ejecutar las pruebas de integración con API keys reales (secretos configurados en GitHub) — **parcialmente**:
  las pruebas en vivo de OpenAI pasan (11 pruebas, incluidas skills, router, artifacts y map-reduce).
- [x] Pipeline de publicación listo con trusted publishing (OIDC, sin API key de larga duración) — ver
  `docs/quality/nuget-trusted-publishing.es.md`; las versiones se publican al hacer push de un tag `vX.Y.Z` (empezando por `v0.1.0`)

## 🚧 Pendiente / ideas

- [ ] Primera versión en nuget.org (`v0.1.0`)
- [ ] Pruebas de integración en vivo para Anthropic, Gemini y Mistral
- [ ] `TimeProvider` en `WorkflowEngine` y pendientes medios/bajos de `docs/quality/test-anti-patterns.es.md`
- [ ] Azure OpenAI con autenticación Entra ID y Vertex AI nativos (hoy: endpoints compatibles o Microsoft.Extensions.AI)
- [ ] Almacén Redis para streams reanudables
- [ ] Detección de tablas en PDF digitales sin OCR (hoy: usar `PdfOcr = PdfOcrMode.Always` con un OCR de modelo)
- [ ] Generative UI y herramientas de servidor adicionales (computer use, memoria)
- [ ] Almacenes `IWorkflowStore` para SQL Server/PostgreSQL/Redis y panel web de ejecuciones
- [ ] Workflows declarativos (JSON) para que los agentes armen flujos completos, no solo tareas
- [ ] Generación de PowerPoint y gráficos en Excel

> [!TIP]
> ¿Quieres ayudar con alguno de estos puntos? Los issues y pull requests son bienvenidos; consulta [Calidad](es-Calidad) para ver los estándares que sigue el proyecto.

---

**Siguiente:** [Preguntas frecuentes](es-Preguntas-frecuentes) ➡️
