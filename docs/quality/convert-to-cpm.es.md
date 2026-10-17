# Informe de conversión a Central Package Management

> 🇬🇧 [Read in English](convert-to-cpm.md)

Generado siguiendo la skill `convert-to-cpm` de [dotnet/skills](https://github.com/dotnet/skills).

## 1. Resumen

- **Alcance**: todos los proyectos de `AiSdk.slnx` (19 proyectos: 15 librerías, 2 de pruebas y 2 ejemplos).
- **Objetivo CLI** usado en la línea base y la validación: `AiSdk.slnx` (SDK 10.0.402).
- **Ámbito CPM**: `Directory.Packages.props` en la raíz del repositorio gobierna todos los proyectos.
- **Paquetes centralizados**: 19.
- **Omitidos**: ninguno.
- **Propiedades MSBuild de versión**: no había (nada que integrar ni eliminar).
- **Archivos compartidos inspeccionados**: `Directory.Build.props` (sin `PackageReference`; sin cambios).
- **Referencias condicionales**: no había.

## 2. Conflictos de versión

Las versiones ya eran consistentes: los paquetes usados en más de un proyecto (`xunit`, `xunit.runner.visualstudio`,
`Xunit.SkippableFact`, `Microsoft.NET.Test.Sdk`) tenían la misma versión en todos.

## 3. Comparación de paquetes (antes / después)

**Cambios**: ninguno. La conversión es neutra en versiones: se compararon 133 entradas (paquetes directos y
transitivos por proyecto) y todas resuelven la misma versión.

**Sin cambios (paquetes directos)**

| Proyecto | Framework | Paquete | Versión |
|---|---|---|---|
| `src/AiSdk.Documents/AiSdk.Documents.csproj` | net10.0 | DocumentFormat.OpenXml | 3.5.1 |
| `src/AiSdk.Documents/AiSdk.Documents.csproj` | net10.0 | Markdig | 1.4.0 |
| `src/AiSdk.Documents/AiSdk.Documents.csproj` | net10.0 | PdfPig | 0.1.16 |
| `src/AiSdk.Documents/AiSdk.Documents.csproj` | net10.0 | ReverseMarkdown | 6.2.1 |
| `src/AiSdk.Extensions.AI/AiSdk.Extensions.AI.csproj` | net10.0 | Microsoft.Extensions.AI.Abstractions | 10.11.0 |
| `src/AiSdk.Extensions.DependencyInjection/AiSdk.Extensions.DependencyInjection.csproj` | net10.0 | Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 |
| `src/AiSdk.Extensions.DependencyInjection/AiSdk.Extensions.DependencyInjection.csproj` | net10.0 | Microsoft.Extensions.Http | 10.0.0 |
| `src/AiSdk.PgVector/AiSdk.PgVector.csproj` | net10.0 | Npgsql | 10.0.3 |
| `src/AiSdk.PgVector/AiSdk.PgVector.csproj` | net10.0 | Pgvector | 0.3.2 |
| `src/AiSdk.VectorData/AiSdk.VectorData.csproj` | net10.0 | Microsoft.Extensions.VectorData.Abstractions | 10.11.0 |
| `src/AiSdk.Workflows/AiSdk.Workflows.csproj` | net10.0 | Cronos | 0.13.0 |
| `src/AiSdk.Workflows/AiSdk.Workflows.csproj` | net10.0 | Microsoft.Extensions.Hosting.Abstractions | 10.0.0 |
| `tests/AiSdk.IntegrationTests/AiSdk.IntegrationTests.csproj` | net10.0 | Microsoft.NET.Test.Sdk | 17.14.1 |
| `tests/AiSdk.IntegrationTests/AiSdk.IntegrationTests.csproj` | net10.0 | Xunit.SkippableFact | 1.5.23 |
| `tests/AiSdk.IntegrationTests/AiSdk.IntegrationTests.csproj` | net10.0 | xunit | 2.9.3 |
| `tests/AiSdk.IntegrationTests/AiSdk.IntegrationTests.csproj` | net10.0 | xunit.runner.visualstudio | 3.1.4 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | CommunityToolkit.VectorData.InMemory | 1.0.1 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | Microsoft.AspNetCore.TestHost | 10.0.0 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | Microsoft.NET.Test.Sdk | 17.14.1 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | Xunit.SkippableFact | 1.5.23 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | coverlet.collector | 6.0.4 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | xunit | 2.9.3 |
| `tests/AiSdk.Tests/AiSdk.Tests.csproj` | net10.0 | xunit.runner.visualstudio | 3.1.4 |

## 4. Riesgo

**Bajo**: conversión neutra en versiones; restore y build terminaron sin errores ni advertencias. No hay
`VersionOverride`, propiedades eliminadas ni cambios condicionales. Como no cambió ninguna versión resuelta, la
skill no exige ejecutar pruebas; igualmente se ejecutó la suite completa después de la conversión.

## 5. Seguimiento

1. Actualizar versiones en un único lugar (`Directory.Packages.props`) en próximas mejoras de dependencias.
2. Evaluar `CentralPackageTransitivePinningEnabled` si se necesita fijar versiones transitivas por seguridad.

## 6. Artefactos

En `artifacts/convert-to-cpm/` (no versionados; se regeneran con los comandos de la skill):

- `baseline.binlog` / `after-cpm.binlog`: compilación antes y después, para comparar con MSBuild Structured Log Viewer.
- `baseline-packages.json` / `after-cpm-packages.json`: paquetes resueltos, legibles por máquina.
- Este informe (`docs/quality/convert-to-cpm.es.md`; versión en inglés: `docs/quality/convert-to-cpm.md`).

No se requiere ninguna acción antes de integrar.
