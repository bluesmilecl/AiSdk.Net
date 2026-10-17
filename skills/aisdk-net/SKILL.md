---
name: aisdk-net
description: >-
  Escribe código C# con AiSdk.Net (paquetes AiSdk.Net.*, namespaces AiSdk.*): agentes, herramientas,
  streaming con useChat, salida estructurada, RAG, orquestación multi-agente, workflows, skills y
  generación de documentos. USE FOR: "usar AiSdk", "agente en .NET con AiSdk", Ai.GenerateTextAsync,
  ToolLoopAgent, Tool.Create, MapWorkflows, SkillLibrary, AnthropicProvider, errores de compilación o
  de comportamiento con estas APIs. DO NOT USE FOR: Semantic Kernel, Microsoft.Extensions.AI sin
  AiSdk, el SDK oficial de OpenAI o Anthropic para .NET, ni código TypeScript del AI SDK de Vercel.
license: MIT
compatibility: Requiere .NET 10 y los paquetes AiSdk.Net.* desde NuGet o por referencia de proyecto.
metadata:
  repository: bluesmilecl/sdk-agent-ia-net
---

# AiSdk.Net

Guía para escribir código correcto con AiSdk.Net a la primera. Cubre las decisiones donde un modelo
sin esta skill suele equivocarse: nombres de paquetes, el bucle de herramientas, opciones de proveedor,
pruebas sin API key y las reglas de los workflows durables.

## When to Use

- Crear o modificar agentes, herramientas, endpoints de chat o workflows con AiSdk.Net.
- Corregir errores de compilación o comportamiento inesperado con estas APIs.

## When Not to Use

- Proyectos que usan otro SDK de IA (Semantic Kernel, SDK oficial de OpenAI/Anthropic).

## Workflow

1. Detecta qué paquetes usa el proyecto (`dotnet list package` o los `.csproj`). Los paquetes se llaman
   `AiSdk.Net.*`, pero los `using` son `AiSdk.*`:

   ```bash
   dotnet add package AiSdk.Net.Anthropic   # using AiSdk; using AiSdk.Anthropic;
   ```

2. Elige la API según la tarea (detalle en [references/api-map.md](references/api-map.md)):

   | Necesidad | Usa | No uses |
   |---|---|---|
   | Una respuesta | `Ai.GenerateTextAsync(new GenerateTextOptions { ... })` | `HttpClient` directo |
   | Herramientas con varios pasos | `StopWhen = StopCondition.StepCountIs(n)` | omitir `StopWhen` (se detiene tras 1 paso) |
   | Agente reutilizable | `ToolLoopAgent` o una clase que herede `Agent` | repetir opciones en cada llamada |
   | Objeto tipado | `Ai.GenerateObjectAsync<T>` | parsear JSON a mano |
   | Chat web con `useChat` | `agent.Stream(...).ToUIMessageStreamResult()` | SSE manual |
   | Varios agentes | `Orchestrations.Sequential/Parallel/Router/Supervisor/Handoffs` | anidar llamadas a mano |
   | Procesos largos o con aprobación | `AiSdk.Workflows` (`ctx.StepAsync`, `WaitForEventAsync`) | `Task.Delay` largos |

3. Opciones específicas del proveedor van en `ProviderOptions`, creadas con los helpers del paquete:

   ```csharp
   ProviderOptions = AnthropicOptions.Create(effort: "medium", refusalFallback: true),
   ```

4. Para pruebas usa `MockLanguageModel` (sin API key ni red); cada `Returns*` responde una llamada en orden:

   ```csharp
   var model = new MockLanguageModel()
       .ReturnsToolCall("get_weather", new { city = "Santiago" })
       .ReturnsText("Hay 20 °C.");
   ```

5. En Minimal APIs, una lambda cuyo único parámetro es `HttpContext` se toma como `RequestDelegate` y su
   `IResult` se descarta. Agrega otro parámetro (p. ej. `CancellationToken ct`).

6. En workflows, todo efecto secundario (modelo, API, correo) va dentro de `ctx.StepAsync("nombre", ...)`:
   el código se re-ejecuta al reanudar y los pasos completados devuelven su resultado guardado.

7. Compila y ejecuta las pruebas. Informa los errores tal como aparecen; no declares éxito sin compilar.

## Validation

- [ ] `dotnet build` sin errores y las pruebas pasan con `MockLanguageModel`.
- [ ] Los agentes con herramientas tienen `StopWhen` (o heredan el valor por defecto de `Agent`: 20 pasos).
- [ ] Ninguna API key quedó en el código (se leen de variables de entorno: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`...).

## Common Pitfalls

| Problema | Solución |
|---|---|
| `using AiSdk.Net` no compila | Los namespaces son `AiSdk`, `AiSdk.Anthropic`, `AiSdk.Workflows`... |
| La herramienta se llama pero no hay respuesta final | Falta `StopWhen` en `GenerateTextOptions` |
| `ArgumentException` por ruta en una skill | Las rutas de archivos de skills son relativas y sin `..` |
| Scripts de skills no se ejecutan | Requieren `SkillToolOptions.ScriptRunner` y skills con `Trusted = true` |
| El modelo no activa una skill | Mejora la descripción: qué hace, USE FOR y DO NOT USE FOR |
