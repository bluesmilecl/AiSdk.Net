using System.Globalization;
using System.Runtime.CompilerServices;

namespace AiSdk.Tests;

/// <summary>
/// Las pruebas no dependen del idioma de la máquina: los mensajes del SDK salen en inglés (recursos neutrales) salvo
/// en las pruebas que fijan otra cultura a propósito.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
    }
}
