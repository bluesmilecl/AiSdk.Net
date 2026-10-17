using System.ComponentModel;

namespace QuickStart;

/// <summary>Venta de ejemplo (generación de Excel).</summary>
internal sealed record Sale(string Producto, DateTime Fecha, int Unidades, decimal Monto);

/// <summary>Decisión de aprobación enviada como evento a un workflow.</summary>
internal sealed record Approval(bool Approved, string? Audience = null);

internal sealed record WeatherQuery([property: Description("Nombre de la ciudad")] string City);

internal sealed record Weather(string City, int TemperatureC, string Conditions);

[Description("Una receta de cocina")]
internal sealed record Recipe(
    [property: Description("Nombre de la receta")] string Name,
    [property: Description("Lista de ingredientes")] List<string> Ingredients,
    [property: Description("Tiempo de preparación en minutos")] int Minutes);
