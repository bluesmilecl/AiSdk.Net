# Central Package Management Conversion Report

> 🇪🇸 [Leer en español](convert-to-cpm.es.md)

Generated following the `convert-to-cpm` skill from [dotnet/skills](https://github.com/dotnet/skills).

## 1. Summary

- **Scope**: every project in `AiSdk.slnx` (19 projects: 15 libraries, 2 test projects and 2 samples).
- **CLI target** used for the baseline and the validation: `AiSdk.slnx` (SDK 10.0.402).
- **CPM scope**: `Directory.Packages.props` at the repository root governs every project.
- **Centralized packages**: 19.
- **Skipped**: none.
- **Version MSBuild properties**: there were none (nothing to inline or remove).
- **Shared files inspected**: `Directory.Build.props` (no `PackageReference`; unchanged).
- **Conditional references**: there were none.

## 2. Version conflicts

Versions were already consistent: the packages used in more than one project (`xunit`, `xunit.runner.visualstudio`,
`Xunit.SkippableFact`, `Microsoft.NET.Test.Sdk`) had the same version everywhere.

## 3. Package comparison (before / after)

**Changes**: none. The conversion is version-neutral: 133 entries (direct and
transitive packages per project) were compared and all of them resolve to the same version.

**Unchanged (direct packages)**

| Project | Framework | Package | Version |
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

## 4. Risk

**Low**: version-neutral conversion; restore and build finished with no errors or warnings. There are no
`VersionOverride`s, removed properties or conditional changes. Since no resolved version changed, the
skill does not require running tests; the full suite was run after the conversion anyway.

## 5. Follow-up

1. Update versions in a single place (`Directory.Packages.props`) in future dependency upgrades.
2. Consider `CentralPackageTransitivePinningEnabled` if transitive versions need to be pinned for security.

## 6. Artifacts

In `artifacts/convert-to-cpm/` (not under version control; regenerated with the skill's commands):

- `baseline.binlog` / `after-cpm.binlog`: build before and after, to compare with MSBuild Structured Log Viewer.
- `baseline-packages.json` / `after-cpm-packages.json`: resolved packages, machine-readable.
- This report (`docs/quality/convert-to-cpm.md`; Spanish version: `docs/quality/convert-to-cpm.es.md`).

No action is required before merging.
