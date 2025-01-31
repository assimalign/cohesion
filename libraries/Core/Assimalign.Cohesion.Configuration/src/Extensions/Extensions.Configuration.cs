﻿using Assimalign.Cohesion.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Extension methods for configuration classes./>.
/// </summary>
public static class ConfigurationExtensions
{
    private const BindingFlags DeclaredOnlyLookup = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="path"></param>
    /// <returns></returns>
    public static T Get<T>(this IConfiguration configuration, KeyPath path)
    {
        if (typeof(T) == IConfigurationSection)
        {
            var str = new string[0]; 
        }

        

        throw new NotImplementedException();
    }

    public static object Get(this IConfiguration configuration, Type type, KeyPath path)
    {


        throw new NotImplementedException();
    }



    public IEnumerable<>

    
   
    
    private static object? CreateInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
    {
        if (type.IsInterface || type.IsAbstract)
        {
            throw new InvalidOperationException();// SR.Format(SR.Error_CannotActivateAbstractOrInterface, type));
        }

        if (type.IsAssignableTo(typeof(ICollection)))
        {
            if (type.GetArrayRank() > 1)
            {
                throw new InvalidOperationException();// SR.Format(SR.Error_UnsupportedMultidimensionalArray, type));
            }

            return Array.CreateInstance(type.GetElementType(), 0);
        }

        if (!type.IsValueType)
        {
            bool hasDefaultConstructor = type.GetConstructors(DeclaredOnlyLookup).Any(ctor => ctor.IsPublic && ctor.GetParameters().Length == 0);
            if (!hasDefaultConstructor)
            {
                throw new InvalidOperationException();// SR.Format(SR.Error_MissingParameterlessConstructor, type));
            }
        }

        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("");// Resources.ExceptionMessageFailedToActivate);// SR.Format(SR.Error_FailedToActivate, type), ex);
        }
    }




    ///// <summary>
    ///// 
    ///// </summary>
    ///// <typeparam name="T"></typeparam>
    ///// <param name="path"></param>
    ///// <param name="value"></param>
    ///// <returns></returns>
    //public static bool TryGet<T>(this IConfiguration configuration, ConfigurationPath path, out T value)
    //{
    //    throw new NotImplementedException();
    //}
    ///// <summary>
    ///// Tries to get a configuration value for the specified key.
    ///// </summary>
    ///// <param name="path">The key.</param>
    ///// <param name="value">The value.</param>
    ///// <returns><c>True</c> if a value for the specified key was found, otherwise <c>false</c>.</returns>
    //public static bool TryGet(this IConfiguration configuration, ConfigurationPath path, out object value)
    //{
    //    throw new NotImplementedException();
    //}

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configuration"></param>
    /// <param name="path"></param>
    /// <returns></returns>
    public static IConfigurationSection GetSection(this IConfiguration configuration, KeyPath path)
    {
        var entry = configuration[path];

        if (entry is null)
        {
            // TODO: Config exception
        }

        if (entry is not IConfigurationSection section)
        {
            throw ThrowHelper.GetConfigurationException("");
        }

        return section;
    }


    ///// <summary>
    ///// Adds a new configuration source.
    ///// </summary>
    ///// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    ///// <param name="configureSource">Configures the source secrets.</param>
    ///// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    //public static IConfigurationBuilder Add<TSource>(this IConfigurationBuilder builder, Action<TSource> configureSource) where TSource : IConfigurationSource, new()
    //{
    //    var source = new TSource();
    //    configureSource?.Invoke(source);
    //    return builder.Add(source);
    //}

    ///// <summary>
    ///// Shorthand for GetSection("ConnectionStrings")[name].
    ///// </summary>
    ///// <param name="configuration">The configuration.</param>
    ///// <param name="name">The connection string key.</param>
    ///// <returns>The connection string.</returns>
    //public static string GetConnectionString(this IConfiguration configuration, string name)
    //{
    //    return configuration?.GetSection("ConnectionStrings")?[name];
    //}

    ///// <summary>
    ///// Get the enumeration of key value pairs within the <see cref="IConfiguration" />
    ///// </summary>
    ///// <param name="configuration">The <see cref="IConfiguration"/> to enumerate.</param>
    ///// <returns>An enumeration of key value pairs.</returns>
    //public static IEnumerable<KeyValuePair<string, string>> AsEnumerable(this IConfiguration configuration) => configuration.AsEnumerable(makePathsRelative: false);

    ///// <summary>
    ///// Get the enumeration of key value pairs within the <see cref="IConfiguration" />
    ///// </summary>
    ///// <param name="configuration">The <see cref="IConfiguration"/> to enumerate.</param>
    ///// <param name="makePathsRelative">If true, the child keys returned will have the current configuration's Path trimmed from the front.</param>
    ///// <returns>An enumeration of key value pairs.</returns>
    //public static IEnumerable<KeyValuePair<string, string>> AsEnumerable(this IConfiguration configuration, bool makePathsRelative)
    //{
    //    var stack = new Stack<IConfiguration>();
    //    stack.Push(configuration);
    //    var rootSection = configuration as IConfigurationSection;
    //    int prefixLength = (makePathsRelative && rootSection != null) ? rootSection.Path.Length + 1 : 0;
    //    while (stack.Count > 0)
    //    {
    //        IConfiguration config = stack.Pop();
    //        // Don't include the sections value if we are removing paths, since it will be an empty key
    //        if (config is IConfigurationSection section && (!makePathsRelative || config != configuration))
    //        {
    //            yield return new KeyValuePair<string, string>(section.Path.Substring(prefixLength), section.Value);
    //        }
    //        foreach (IConfigurationSection child in config.GetChildren())
    //        {
    //            stack.Push(child);
    //        }
    //    }
    //}

    ///// <summary>
    ///// Determines whether the section has a <see cref="IConfigurationSection.Value"/> or has children
    ///// </summary>
    //public static bool Exists(this IConfigurationSection section)
    //{
    //    if (section == null)
    //    {
    //        return false;
    //    }
    //    return section.Value != null || section.GetChildren().Any();
    //}

    ///// <summary>
    ///// Gets a configuration sub-section with the specified key.
    ///// </summary>
    ///// <param name="configuration"></param>
    ///// <param name="key">The key of the configuration section.</param>
    ///// <returns>The <see cref="IConfigurationSection"/>.</returns>
    ///// <remarks>
    /////     If no matching sub-section is found with the specified key, an exception is raised.
    ///// </remarks>
    ///// <exception cref="System.InvalidOperationException">There is no section with key <paramref name="key"/>.</exception>
    //public static IConfigurationSection GetRequiredSection(this IConfiguration configuration, string key)
    //{
    //    if (configuration == null)
    //    {
    //        throw new ArgumentNullException(nameof(configuration));
    //    }

    //    IConfigurationSection section = configuration.GetSection(key);
    //    if (section.Exists())
    //    {
    //        return section;
    //    }

    //    throw new InvalidOperationException($"The section does not exist in {key}");
    //}
}
