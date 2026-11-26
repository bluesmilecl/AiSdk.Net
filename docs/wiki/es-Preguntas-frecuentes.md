# ❓ Preguntas frecuentes

> 🇬🇧 [Read in English](FAQ)

<details open>
<summary><b>¿En qué se diferencia AiSdk.Net de Microsoft.Extensions.AI o Semantic Kernel?</b></summary>

Microsoft.Extensions.AI ofrece abstracciones de bajo nivel (`IChatClient`); AiSdk.Net construye un kit completo
sobre una abstracción similar: bucle de agentes, orquestación multi-agente, Skills, artifacts, workflows durables,
RAG, conversión de documentos y un backend para `useChat`. Se complementan en ambas direcciones: cualquier
`IChatClient` se puede usar como modelo de AiSdk.Net con `AsLanguageModel()`, y cualquier modelo de AiSdk.Net se puede
usar como `IChatClient` con `AsIChatClient()` (Semantic Kernel, Microsoft Agent Framework…); ver
[ASP.NET Core y useChat](es-ASP.NET-Core-y-useChat#microsoftextensionsai).
Si conoces el AI SDK de Vercel o Laravel AI, la API te resultará familiar ([Equivalencias](es-Equivalencias)).
</details>

<details>
<summary><b>¿Qué proveedores soporta?</b></summary>

Nativos: OpenAI, Anthropic Claude, Google Gemini, Mistral, Amazon Bedrock y Cohere. Cualquier servidor compatible
con OpenAI (Ollama, Groq, DeepSeek, OpenRouter, LM Studio, vLLM…) funciona con `openai.Chat(...)`, y cualquier
cliente de Microsoft.Extensions.AI funciona con el adaptador. Ver [Proveedores](es-Proveedores).
</details>

<details>
<summary><b>¿Puedo probarlo sin API key?</b></summary>

Sí. `MockLanguageModel` simula respuestas y llamadas a herramientas, y todos los ejemplos funcionan con él:
`dotnet run --project samples/QuickStart -- mock agent`.
</details>

<details>
<summary><b>¿Puedo usar modelos locales?</b></summary>

Sí, con Ollama, LM Studio o vLLM mediante el proveedor compatible con OpenAI. El OCR también puede ser local con
Tesseract (ver [Documentos, OCR y contexto](es-Documentos-OCR-y-contexto)).
</details>

<details>
<summary><b>¿Por qué los paquetes se llaman <code>AiSdk.Net.*</code> y los namespaces <code>AiSdk.*</code>?</b></summary>

El id `AiSdk` ya existía en nuget.org, por eso los paquetes usan el prefijo `AiSdk.Net`. El código mantiene
namespaces cortos: `using AiSdk;`, `using AiSdk.OpenAI;`.
</details>

<details>
<summary><b>¿Está listo para producción?</b></summary>

Sí. La versión 1.0 congela la API pública (controlada con analizadores de API pública) y sigue el Versionado
Semántico: no habrá cambios incompatibles hasta la 2.0. Trae más de 550 pruebas unitarias, pruebas de integración con
PostgreSQL y pruebas en vivo. Ver [Versiones y compatibilidad](es-Calidad#versiones-y-compatibilidad) y las notas de
versión en `CHANGELOG.md`.
</details>

<details>
<summary><b>¿El SDK envía telemetría a algún lado?</b></summary>

No. Solo emite spans y métricas en el `ActivitySource` y el `Meter` `"AiSdk"` (convenciones GenAI de OpenTelemetry,
sin prompts ni respuestas); no salen de tu aplicación salvo que configures OpenTelemetry para exportarlos (ver
[Pruebas y telemetría](es-Pruebas-y-telemetria)).
</details>

<details>
<summary><b>¿En qué idioma salen los errores y avisos?</b></summary>

En inglés por defecto y en español cuando `CultureInfo.CurrentUICulture` es español (`es`, `es-CL`, `es-ES`...): los
textos en español viajan como ensamblados satélite dentro de los paquetes. Vale también para los textos que recibe el
modelo (por ejemplo, cuando el usuario deniega una herramienta). En ASP.NET Core, fija la cultura con
`UseRequestLocalization` o `CultureInfo.DefaultThreadCurrentUICulture`. Los mensajes son para personas y modelos: no
los interpretes con código.
</details>

<details>
<summary><b>¿Son seguras las Skills de terceros?</b></summary>

Las skills cargadas desde GitHub o zip no son confiables por defecto: el agente puede leer sus instrucciones, pero
no ejecutar sus scripts salvo que las marques como confiables o lo habilites explícitamente. Las rutas se validan
contra path traversal. Ver [Skills](es-Skills).
</details>

<details>
<summary><b>¿Cómo puedo contribuir?</b></summary>

Lee [CONTRIBUTING.es.md](https://github.com/bluesmilecl/AiSdk.Net/blob/HEAD/CONTRIBUTING.es.md), elige un
[`good first issue`](https://github.com/bluesmilecl/AiSdk.Net/labels/good%20first%20issue) o mejora esta
documentación en `docs/wiki`. Las preguntas son bienvenidas en
[Discussions](https://github.com/bluesmilecl/AiSdk.Net/discussions).
</details>

<details>
<summary><b>¿Quién creó AiSdk.Net y cuál es la licencia?</b></summary>

Lo crearon **Bluesmile** ([@bluesmilecl](https://github.com/bluesmilecl)) y **Esteban Cortes**
([@ecr17dev](https://github.com/ecr17dev)), y se publica bajo la
[licencia MIT](https://github.com/bluesmilecl/AiSdk.Net/blob/HEAD/LICENSE).
</details>

---

⬅️ Volver al [Inicio](es-Inicio)
