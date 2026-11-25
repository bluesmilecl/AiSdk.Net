namespace AiSdk.Tests;

/// <summary>
/// Pruebas que verifican que un caso hostil termina rápido (sin trabajo cuadrático): miden tiempo de CPU, así que
/// corren solas, después de las demás, para que la carga del resto de la suite en paralelo no las vuelva inestables.
/// </summary>
[CollectionDefinition(nameof(PerformanceSensitive), DisableParallelization = true)]
public sealed class PerformanceSensitive;
