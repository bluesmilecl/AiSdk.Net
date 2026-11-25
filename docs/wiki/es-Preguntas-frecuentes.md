# ❓ Preguntas frecuentes

> 🇬🇧 [Read in English](FAQ)

<details open>
<summary><b>¿En qué se diferencia AiSdk.Net de Microsoft.Extensions.AI o Semantic Kernel?</b></summary>

Microsoft.Extensions.AI ofrece abstracciones de bajo nivel (`IChatClient`); AiSdk.Net construye un kit completo
sobre una abstracción similar: bucle de agentes, orquestación multi-agente, Skills, artifacts, workflows durables,
RAG, conversión de documentos y un backend para `useChat`. Se complementan: cualquier `IChatClient` se puede usar
como modelo de AiSdk.Net con `AsLanguageModel()` (ver [ASP.NET Core y useChat](es-ASP.NET-Core-y-useChat)).
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

El proyecto está en versión 0.x: la API está completa y probada (más de 450 pruebas unitarias y pruebas en vivo),
pero puede cambiar antes de la 1.0. Fija versiones y revisa la [Hoja de ruta](es-Hoja-de-ruta) y las notas de
versión en `CHANGELOG.md`.
</details>

<details>
<summary><b>¿El SDK envía telemetría a algún lado?</b></summary>

No. Solo emite spans y métricas en el `ActivitySource` y el `Meter` `"AiSdk"` (convenciones GenAI de OpenTelemetry,
sin prompts ni respuestas); no salen de tu aplicación salvo que configures OpenTelemetry para exportarlos (ver
[Pruebas y telemetría](es-Pruebas-y-telemetria)).
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
