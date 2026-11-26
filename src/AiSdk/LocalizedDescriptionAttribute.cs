using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace AiSdk;

/// <summary>
/// A <see cref="DescriptionAttribute"/> whose text comes from localized resources, looked up each time it is read with
/// <see cref="CultureInfo.CurrentUICulture"/>. Tool and schema descriptions sent to the model (and any other place that
/// reads <see cref="DescriptionAttribute.Description"/>) then follow the culture of the caller.
/// </summary>
/// <remarks>
/// <paramref name="resourceType"/> is a strongly typed resource class (for example the one generated from a
/// <c>Strings.resx</c> file); it may be internal. Its static <c>ResourceManager</c> property is used to read
/// <paramref name="resourceName"/>. If the resource does not exist, the resource name is returned.
/// </remarks>
/// <param name="resourceType">The strongly typed resource class that exposes a static <c>ResourceManager</c> property.</param>
/// <param name="resourceName">The name of the string resource (use <c>nameof</c>).</param>
/// <example>
/// <code>
/// public sealed record WeatherInput(
///     [property: LocalizedDescription(typeof(Strings), nameof(Strings.WeatherCity))] string City);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.All)]
public sealed class LocalizedDescriptionAttribute(Type resourceType, string resourceName) : DescriptionAttribute
{
    private static readonly ConcurrentDictionary<Type, ResourceManager?> s_managers = new();

    /// <summary>Gets the strongly typed resource class.</summary>
    public Type ResourceType { get; } = resourceType ?? throw new ArgumentNullException(nameof(resourceType));

    /// <summary>Gets the name of the string resource.</summary>
    public string ResourceName { get; } = resourceName ?? throw new ArgumentNullException(nameof(resourceName));

    /// <summary>Gets the localized description for <see cref="CultureInfo.CurrentUICulture"/>.</summary>
    public override string Description =>
        s_managers.GetOrAdd(ResourceType, FindManager)?.GetString(ResourceName, CultureInfo.CurrentUICulture) ?? ResourceName;

    private static ResourceManager? FindManager(Type type) =>
        type.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as ResourceManager;
}
