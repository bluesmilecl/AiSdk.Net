# 🏛️ Arquitectura

> 🇬🇧 [Read in English](Architecture)

<img src="https://raw.githubusercontent.com/bluesmilecl/AiSdk.Net/HEAD/docs/assets/architecture.es.svg" alt="Arquitectura de AiSdk.Net" width="100%">

AiSdk.Net es un conjunto de paquetes pequeños alrededor de un núcleo. Tu aplicación usa la **API del núcleo**
(`Ai.*`, agentes, orquestaciones); el núcleo habla con los modelos solo a través de la abstracción
`ILanguageModel`, así que proveedores, middleware y dobles de prueba son intercambiables.

## 🧱 Capas

```mermaid
flowchart TB
    subgraph App["Tu aplicación"]
        Web["ASP.NET Core / useChat"]
        Console["Consola / Workers"]
    end
    subgraph Core["AiSdk.Net (núcleo)"]
        Api["Ai.GenerateTextAsync / StreamText / GenerateObjectAsync"]
        Agents["ToolLoopAgent · Agent · Orquestaciones"]
        Feat["Herramientas · Skills · Artifacts · Memoria · RAG · Compresión"]
        Mw["Pipeline de middleware"]
        Abs[["ILanguageModel · IEmbeddingModel · IVectorStore"]]
    end
    subgraph Ext["Integraciones"]
        Wf["AiSdk.Net.Workflows"]
        Docs["AiSdk.Net.Documents"]
        Mcp["AiSdk.Net.Mcp"]
        Vec["AiSdk.Net.PgVector / VectorData"]
    end
    subgraph Prov["Proveedores"]
        OpenAI
        Anthropic
        Google
        Mistral
        Bedrock
        Cohere
        MEAI["Microsoft.Extensions.AI"]
    end
    App --> Agents
    App --> Api
    Agents --> Api
    Agents --> Feat
    Api --> Mw --> Abs
    Abs --> Prov
    Ext --> Abs
    Wf --> Agents
```

## 🔄 Vida de una petición

```mermaid
sequenceDiagram
    autonumber
    participant App as Aplicación
    participant Agent as ToolLoopAgent
    participant Mw as Middleware
    participant Model as ILanguageModel (proveedor)
    participant API as API HTTP del proveedor
    App->>Agent: Stream("pregunta")
    loop hasta StopWhen
        Agent->>Mw: LanguageModelRequest (mensajes, herramientas, opciones)
        Mw->>Model: petición transformada (defaults, logs, guardrails)
        Model->>API: JSON del proveedor (con reintentos)
        API-->>Model: stream SSE
        Model-->>Agent: StreamPart (texto, razonamiento, llamadas a herramientas)
        opt llamadas a herramientas
            Agent->>Agent: ejecutar herramientas (aprobación, reparación)
        end
    end
    Agent-->>App: TextStream / stream de mensajes UI
```

## 📁 Estructura del repositorio

```
src/
  AiSdk/                      núcleo
  AiSdk.OpenAI/               proveedor OpenAI + servidores compatibles
  AiSdk.Anthropic/            proveedor Claude
  AiSdk.Google/               proveedor Gemini
  AiSdk.Mistral/              proveedor Mistral
  AiSdk.Bedrock/              proveedor Amazon Bedrock
  AiSdk.Cohere/               reranking y embeddings de Cohere
  AiSdk.Mcp/                  cliente MCP (stdio + HTTP)
  AiSdk.Extensions.AI/        adaptador Microsoft.Extensions.AI
  AiSdk.Extensions.DependencyInjection/
  AiSdk.AspNetCore/
  AiSdk.Documents/            documentos → Markdown, OCR, middleware de adjuntos; generación de Word/PDF/Excel
  AiSdk.Workflows/            workflows durables, triggers y automatizaciones
  AiSdk.PgVector/             almacén PostgreSQL + pgvector
  AiSdk.VectorData/           adaptador Microsoft.Extensions.VectorData
skills/aisdk-net/             Agent Skill que enseña a los agentes de código a usar este SDK
docs/
  assets/                     SVG animados e ícono del paquete
  wiki/                       esta wiki (sincronizada por .github/workflows/wiki.yml)
  quality/                    informes de calidad (CPM, auditoría de pruebas, trusted publishing)
  nuget/                      README incluido en los paquetes NuGet
samples/
  QuickStart/                 ejemplos de consola (usa `mock` para no necesitar API key)
  ChatApi/                    backend para useChat
tests/AiSdk.Tests/            pruebas unitarias (modelos simulados + HTTP falso)
tests/AiSdk.IntegrationTests/ pruebas en vivo contra las APIs reales (opcionales)
```

## 🧭 Principios de diseño

| Principio | En la práctica |
|---|---|
| **Una API, muchos proveedores** | Cada proveedor implementa `ILanguageModel`; cambias de modelo con una línea o con un alias de `ProviderRegistry` |
| **Paquetes pequeños** | El núcleo no depende de ningún proveedor; instalas solo lo que usas |
| **Nombres conocidos** | Sigue el AI SDK de Vercel (`generateText` → `GenerateTextAsync`) y los conceptos de Laravel AI, ver [Equivalencias](es-Equivalencias) |
| **Fácil de probar** | `MockLanguageModel` y handlers HTTP falsos; las pruebas unitarias no usan red |
| **Observable** | Spans y métricas en cada llamada, herramienta y agente, con las convenciones GenAI de OpenTelemetry |
| **Seguro por defecto** | Las skills no confiables no ejecutan scripts, se rechaza path traversal, se verifican los secretos de webhooks |

---

**Siguiente:** [Calidad](es-Calidad) ➡️
