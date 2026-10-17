# Auditoría de pruebas (skill `dotnet-test:test-anti-patterns`)

Revisión de 210 métodos de prueba (198 unitarios, 12 de integración) con la skill
`test-anti-patterns` de [dotnet/skills](https://github.com/dotnet/skills).

**Resultado:** 0 críticos, 7 altos, 2 medios, 7 bajos. Los 7 altos están corregidos.

## Altos (corregidos)

| # | Prueba | Problema | Corrección |
|---|---|---|---|
| H1 | `ResumableStreamCanBeReadAgainAfterDisconnect` | `Task.Delay(50)` esperando que el chat quedara sin stream activo | **Producción:** `ResumableUIMessageStream` libera el chat antes de completar el stream; se quita el retardo |
| H2 | `AnswersRootsSamplingAndElicitationRequests` | `Task.Delay(100)` esperando una notificación MCP | Sondeo acotado (máx. 5 s) |
| H3 | `ScheduledAutomationsFireOnCronOnceAndEvents` | Asumía 24 h entre ejecuciones; falla en cambios de horario de Chile | Compara fecha y hora local en `America/Santiago` |
| H4 | `PdfFallsBackForMissingGlyphsAndCustomFonts` | Ruta de fuente de Linux fija | DejaVuSans (con su licencia) en `Fixtures/` |
| H5 | `LaunchesRealStdioProcess` | `return` silencioso sin python3 (pasaba sin probar nada) | `Assert.SkipWhen` |
| H6 | `LoadsFromDirectoriesAndSavesSkills` | `ContinueWith` aceptaba cualquier excepción | `Assert.ThrowsAsync<ArgumentException>` |
| H7 | `ScriptsDoNotInheritSecretsAndTimeOut` | Timeout de 500 ms aplicado también al script que debe terminar | Runner por defecto para `env.py`; el corto solo para `lento.py` |

También se corrigieron L1 (detección de python3 por `PATH`) y L2 (variable de entorno restaurada en `finally`),
y `SummarizeUsesMapReduceForLongTexts`, que dependía del orden de llegada de llamadas paralelas.

## Pendientes (medios y bajos)

- **M1:** `WorkflowEngine` lee `DateTimeOffset.UtcNow` directamente; agregar `TimeProvider` a las opciones
  permitiría probar el cron en el cambio de horario sin editar estado interno.
- **M2:** nueve pruebas largas que verifican varios comportamientos; dividir al modificarlas.
- **L3–L7:** sondeo de 1 s en `WorkflowTests`, aserciones débiles (solapamiento del chunker, texto de warnings,
  `Assert.All` sobre listas posiblemente vacías), nombre de `OtherConversationsCannotEditArtifacts`,
  `HttpClient`/scopes sin `using`, y tablas `aisdk_test_*` que quedan en PostgreSQL.

## Fortalezas observadas

Pruebas de proveedores que capturan la petición HTTP real y comparan URL, cabeceras y JSON exactos; valores
esperados de fuentes independientes (vector de prueba de AWS SigV4, CRC32 `0xCBF43926`); excepciones con tipo
específico; fakes pequeños y deterministas; directorios temporales únicos y limpiados en `finally`.
