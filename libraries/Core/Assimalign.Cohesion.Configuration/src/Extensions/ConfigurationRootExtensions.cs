using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;

/// <summary>
/// Extension methods for <see cref="IConfigurationRoot"/>.
/// </summary>
public static class ConfigurationRootExtensions
{

    /// <summary>
    /// Gets the named configuration provider.
    /// </summary>
    /// <param name="name">The <see cref="IConfigurationProvider.Name"/>.</param>
    /// <returns></returns>
    public static IConfigurationProvider GetProvider(this IConfigurationRoot configuration, string name)
    {
        return configuration.GetProvider(name, StringComparison.Ordinal);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="name"></param>
    /// <param name="comparison"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public static IConfigurationProvider GetProvider(this IConfigurationRoot configuration, string name, StringComparison comparison)
    {
        ThrowHelper.ThrowIfNull(configuration, nameof(configuration));
        ThrowHelper.ThrowIfNull(name, nameof(name));

        foreach (var provider in configuration.Providers)
        {
            if (name.Equals(provider.Name, comparison))
            {
                return provider;
            }
        }

        throw new InvalidOperationException("");
    }


    ///// <summary>
    ///// Gets the immediate children sub-sections of configuration root based on key.
    ///// </summary>
    ///// <param name="root">Configuration from which to retrieve sub-sections.</param>
    ///// <param name="path">Key of a section of which children to retrieve.</param>
    ///// <returns>Immediate children sub-sections of section specified by key.</returns>
    //internal static IEnumerable<IConfigurationSection> GetChildrenImplementation(this IConfigurationRoot root, string path)
    //{
    //    return root.Providers
    //        .Aggregate(Enumerable.Empty<string>(),
    //            (seed, source) => source.GetChildKeys(seed, path))
    //        .Distinct(StringComparer.OrdinalIgnoreCase)
    //        .Select(key => root.GetSection(path == null ? key : ConfigurationPathHelper.Combine(path, key)));
    //}

    ///// <summary>
    ///// Generates a human-readable view of the configuration showing where each value came from.
    ///// </summary>
    ///// <returns> The debug view. </returns>
    //public static string GetDebugView(this IConfigurationRoot root)
    //{
    //    void RecurseChildren(
    //        StringBuilder stringBuilder,
    //        IEnumerable<IConfigurationSection> children,
    //        string indent)
    //    {
    //        foreach (IConfigurationSection child in children)
    //        {
    //            (string Value, IConfigurationProvider Provider) valueAndProvider = GetValueAndProvider(root, child.Path);

    //            if (valueAndProvider.Provider != null)
    //            {
    //                stringBuilder
    //                    .Append(indent)
    //                    .Append(child.Key)
    //                    .Append('=')
    //                    .Append(valueAndProvider.Value)
    //                    .Append(" (")
    //                    .Append(valueAndProvider.Provider)
    //                    .AppendLine(")");
    //            }
    //            else
    //            {
    //                stringBuilder
    //                    .Append(indent)
    //                    .Append(child.Key)
    //                    .AppendLine(":");
    //            }

    //            RecurseChildren(stringBuilder, child.GetChildren(), indent + "  ");
    //        }
    //    }

    //    var builder = new StringBuilder();

    //    RecurseChildren(builder, root.GetChildren(), "");

    //    return builder.ToString();
    //}

    //private static (string Value, IConfigurationProvider Provider) GetValueAndProvider(
    //    IConfigurationRoot root,
    //    string key)
    //{
    //    foreach (IConfigurationProvider provider in root.Providers.Reverse())
    //    {
    //        if (provider.TryGet(key, out string value))
    //        {
    //            return (value, provider);
    //        }
    //    }

    //    return (null, null);
    //}
}
