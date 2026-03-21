using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Provides reflection-based helpers that bind configuration values into typed object graphs.
/// </summary>
/// <remarks>
/// Binding uses the root configuration view when possible so provider precedence remains intact while
/// still supporting the composite configuration model used by this library.
/// </remarks>
public static class ConfigurationBinder
{
    private const BindingFlags bindingLookup =
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    private const string TrimmingWarningMessage =
        "Binding uses reflection over runtime types, so trimmers cannot statically preserve every required member.";

    private const string InstanceGetTypeTrimmingWarningMessage =
        "Binding an existing instance uses the runtime instance type, so trimmers cannot statically preserve every required member.";

    private const string PropertyTrimmingWarningMessage =
        "Binding properties uses reflected property types, so trimmers cannot statically preserve every required member.";

    private const string DynamicCodeWarningMessage =
        "Binding object graphs can require runtime code generation when AOT compiling, especially for collections, arrays, and runtime-instantiated generic types.";

    /// <summary>
    /// Creates a new instance of <typeparamref name="T"/> from the current configuration root.
    /// </summary>
    /// <typeparam name="T">The target type to bind.</typeparam>
    /// <param name="configuration">The configuration root.</param>
    /// <returns>The bound instance, or the default value for <typeparamref name="T"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(this IConfiguration configuration)
        => configuration.Get<T>(_ => { });

    /// <summary>
    /// Creates a new instance of <typeparamref name="T"/> from the current configuration root.
    /// </summary>
    /// <typeparam name="T">The target type to bind.</typeparam>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    /// <returns>The bound instance, or the default value for <typeparamref name="T"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfiguration configuration,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        object? result = Get(configuration, typeof(T), configureOptions);

        return result is null
            ? default!
            : (T)result;
    }

    /// <summary>
    /// Creates a new instance of <typeparamref name="T"/> from the configuration entry at <paramref name="path"/>.
    /// </summary>
    /// <typeparam name="T">The target type to bind.</typeparam>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="path">The configuration path to bind.</param>
    /// <returns>The bound instance, or the default value for <typeparamref name="T"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfiguration configuration,
        Path path)
        => configuration.Get<T>(path, _ => { });

    /// <summary>
    /// Creates a new instance of <typeparamref name="T"/> from the configuration entry at <paramref name="path"/>.
    /// </summary>
    /// <typeparam name="T">The target type to bind.</typeparam>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="path">The configuration path to bind.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    /// <returns>The bound instance, or the default value for <typeparamref name="T"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfiguration configuration,
        Path path,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        object? result = Get(configuration, typeof(T), path, configureOptions);

        return result is null
            ? default!
            : (T)result;
    }

    /// <summary>
    /// Creates a new instance of <typeparamref name="T"/> from the current configuration entry.
    /// </summary>
    /// <typeparam name="T">The target type to bind.</typeparam>
    /// <param name="entry">The entry to bind.</param>
    /// <returns>The bound instance, or the default value for <typeparamref name="T"/> when the entry does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(this IConfigurationEntry entry)
        => entry.Get<T>(_ => { });

    /// <summary>
    /// Creates a new instance of <typeparamref name="T"/> from the current configuration entry.
    /// </summary>
    /// <typeparam name="T">The target type to bind.</typeparam>
    /// <param name="entry">The entry to bind.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    /// <returns>The bound instance, or the default value for <typeparamref name="T"/> when the entry does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static T Get<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfigurationEntry entry,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        object? result = Get(entry, typeof(T), configureOptions);

        return result is null
            ? default!
            : (T)result;
    }

    /// <summary>
    /// Creates a new instance of the specified <paramref name="type"/> from the current configuration root.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="type">The target type to bind.</param>
    /// <returns>The bound instance, or <see langword="null"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static object? Get(
        this IConfiguration configuration,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type)
        => Get(configuration, type, _ => { });

    /// <summary>
    /// Creates a new instance of the specified <paramref name="type"/> from the current configuration root.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="type">The target type to bind.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    /// <returns>The bound instance, or <see langword="null"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static object? Get(
        this IConfiguration configuration,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(type);

        ConfigurationBinderOptions options = CreateOptions(configureOptions);
        BindingNode source = new(configuration, Path.Empty);

        return BindInstance(type, instance: null, source, options);
    }

    /// <summary>
    /// Creates a new instance of the specified <paramref name="type"/> from the configuration entry at <paramref name="path"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="type">The target type to bind.</param>
    /// <param name="path">The configuration path to bind.</param>
    /// <returns>The bound instance, or <see langword="null"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static object? Get(
        this IConfiguration configuration,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Path path)
        => Get(configuration, type, path, _ => { });

    /// <summary>
    /// Creates a new instance of the specified <paramref name="type"/> from the configuration entry at <paramref name="path"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="type">The target type to bind.</param>
    /// <param name="path">The configuration path to bind.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    /// <returns>The bound instance, or <see langword="null"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static object? Get(
        this IConfiguration configuration,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Path path,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(type);

        BindingNode source = CreateRootNode(configuration, path);

        if (!source.IsDefined)
        {
            return null;
        }

        ConfigurationBinderOptions options = CreateOptions(configureOptions);

        return BindInstance(type, instance: null, source, options);
    }

    /// <summary>
    /// Creates a new instance of the specified <paramref name="type"/> from the current configuration entry.
    /// </summary>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="type">The target type to bind.</param>
    /// <returns>The bound instance, or <see langword="null"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static object? Get(
        this IConfigurationEntry entry,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type)
        => Get(entry, type, _ => { });

    /// <summary>
    /// Creates a new instance of the specified <paramref name="type"/> from the current configuration entry.
    /// </summary>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="type">The target type to bind.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    /// <returns>The bound instance, or <see langword="null"/> when the configuration does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static object? Get(
        this IConfigurationEntry entry,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(type);

        ConfigurationBinderOptions options = CreateOptions(configureOptions);
        BindingNode source = new(entry);

        return BindInstance(type, instance: null, source, options);
    }

    /// <summary>
    /// Binds the current configuration root into the provided <paramref name="instance"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="instance">The instance to populate.</param>
    [RequiresUnreferencedCode(InstanceGetTypeTrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static void Bind(this IConfiguration configuration, object instance)
        => Bind(configuration, instance, _ => { });

    /// <summary>
    /// Binds the current configuration root into the provided <paramref name="instance"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="instance">The instance to populate.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    [RequiresUnreferencedCode(InstanceGetTypeTrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static void Bind(
        this IConfiguration configuration,
        object instance,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instance);

        ConfigurationBinderOptions options = CreateOptions(configureOptions);
        BindingNode source = new(configuration, Path.Empty);

        BindInstance(instance.GetType(), instance, source, options);
    }

    /// <summary>
    /// Binds the configuration entry at <paramref name="path"/> into the provided <paramref name="instance"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="path">The configuration path to bind.</param>
    /// <param name="instance">The instance to populate.</param>
    [RequiresUnreferencedCode(InstanceGetTypeTrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static void Bind(this IConfiguration configuration, Path path, object instance)
        => Bind(configuration, path, instance, _ => { });

    /// <summary>
    /// Binds the configuration entry at <paramref name="path"/> into the provided <paramref name="instance"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="path">The configuration path to bind.</param>
    /// <param name="instance">The instance to populate.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    [RequiresUnreferencedCode(InstanceGetTypeTrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static void Bind(
        this IConfiguration configuration,
        Path path,
        object instance,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instance);

        BindingNode source = CreateRootNode(configuration, path);

        if (!source.IsDefined)
        {
            return;
        }

        ConfigurationBinderOptions options = CreateOptions(configureOptions);

        BindInstance(instance.GetType(), instance, source, options);
    }

    /// <summary>
    /// Binds the current configuration entry into the provided <paramref name="instance"/>.
    /// </summary>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="instance">The instance to populate.</param>
    [RequiresUnreferencedCode(InstanceGetTypeTrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static void Bind(this IConfigurationEntry entry, object instance)
        => Bind(entry, instance, _ => { });

    /// <summary>
    /// Binds the current configuration entry into the provided <paramref name="instance"/>.
    /// </summary>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="instance">The instance to populate.</param>
    /// <param name="configureOptions">Configures binder behavior.</param>
    [RequiresUnreferencedCode(InstanceGetTypeTrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    public static void Bind(
        this IConfigurationEntry entry,
        object instance,
        Action<ConfigurationBinderOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(instance);

        ConfigurationBinderOptions options = CreateOptions(configureOptions);
        BindingNode source = new(entry);

        BindInstance(instance.GetType(), instance, source, options);
    }

    /// <summary>
    /// Gets a scalar value from the configuration root and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target scalar type.</typeparam>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="path">The configuration path to read.</param>
    /// <returns>The converted scalar value.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static T GetValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfiguration configuration,
        Path path)
    {
        object? value = GetValue(configuration, typeof(T), path, MissingValue.Instance);

        return (T)value!;
    }

    /// <summary>
    /// Gets a scalar value from the configuration root and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target scalar type.</typeparam>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="path">The configuration path to read.</param>
    /// <param name="defaultValue">The default value to return when the path does not exist.</param>
    /// <returns>The converted scalar value, or <paramref name="defaultValue"/> when the path does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static T GetValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfiguration configuration,
        Path path,
        T defaultValue)
    {
        object? value = GetValue(configuration, typeof(T), path, defaultValue);

        return value is null
            ? defaultValue
            : (T)value;
    }

    /// <summary>
    /// Gets a scalar value from the current configuration entry and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target scalar type.</typeparam>
    /// <param name="entry">The configuration entry.</param>
    /// <returns>The converted scalar value.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static T GetValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfigurationEntry entry)
    {
        object? value = GetValue(entry, typeof(T), MissingValue.Instance);

        return (T)value!;
    }

    /// <summary>
    /// Gets a scalar value from the current configuration entry and converts it to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The target scalar type.</typeparam>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="defaultValue">The default value to return when the entry does not contain a scalar value.</param>
    /// <returns>The converted scalar value, or <paramref name="defaultValue"/> when the entry does not contain a scalar value.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static T GetValue<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        this IConfigurationEntry entry,
        T defaultValue)
    {
        object? value = GetValue(entry, typeof(T), defaultValue);

        return value is null
            ? defaultValue
            : (T)value;
    }

    /// <summary>
    /// Gets a scalar value from the configuration root and converts it to the specified <paramref name="type"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="type">The target scalar type.</param>
    /// <param name="path">The configuration path to read.</param>
    /// <returns>The converted scalar value.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static object? GetValue(
        this IConfiguration configuration,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Path path)
        => GetValue(configuration, type, path, MissingValue.Instance);

    /// <summary>
    /// Gets a scalar value from the configuration root and converts it to the specified <paramref name="type"/>.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="type">The target scalar type.</param>
    /// <param name="path">The configuration path to read.</param>
    /// <param name="defaultValue">The default value to return when the path does not exist.</param>
    /// <returns>The converted scalar value, or <paramref name="defaultValue"/> when the path does not exist.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static object? GetValue(
        this IConfiguration configuration,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Path path,
        object? defaultValue)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(type);

        BindingNode source = CreateRootNode(configuration, path);

        return GetValueCore(source, type, path, defaultValue);
    }

    /// <summary>
    /// Gets a scalar value from the current configuration entry and converts it to the specified <paramref name="type"/>.
    /// </summary>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="type">The target scalar type.</param>
    /// <returns>The converted scalar value.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static object? GetValue(
        this IConfigurationEntry entry,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type)
        => GetValue(entry, type, MissingValue.Instance);

    /// <summary>
    /// Gets a scalar value from the current configuration entry and converts it to the specified <paramref name="type"/>.
    /// </summary>
    /// <param name="entry">The configuration entry.</param>
    /// <param name="type">The target scalar type.</param>
    /// <param name="defaultValue">The default value to return when the entry does not contain a scalar value.</param>
    /// <returns>The converted scalar value, or <paramref name="defaultValue"/> when the entry does not contain a scalar value.</returns>
    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    public static object? GetValue(
        this IConfigurationEntry entry,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        object? defaultValue)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(type);

        BindingNode source = new(entry);

        return GetValueCore(source, type, entry.Path, defaultValue);
    }

    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    private static object? GetValueCore(
        BindingNode source,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        Path requestedPath,
        object? defaultValue)
    {
        if (!source.IsDefined)
        {
            if (ReferenceEquals(defaultValue, MissingValue.Instance))
            {
                throw new ArgumentNullException(nameof(requestedPath), $"The configuration value at '{FormatPath(requestedPath)}' was not found.");
            }

            return defaultValue;
        }

        string? value = source.Value;

        if (value is null)
        {
            if (ReferenceEquals(defaultValue, MissingValue.Instance))
            {
                throw new ArgumentNullException(nameof(requestedPath), $"The configuration value at '{FormatPath(requestedPath)}' was not found.");
            }

            return defaultValue;
        }

        return ConvertValue(type, value, source.Path);
    }

    private static ConfigurationBinderOptions CreateOptions(Action<ConfigurationBinderOptions>? configureOptions)
    {
        var options = new ConfigurationBinderOptions();

        configureOptions?.Invoke(options);

        return options;
    }

    private static BindingNode CreateRootNode(IConfiguration configuration, Path path)
    {
        if (path.IsEmpty)
        {
            return new BindingNode(configuration, Path.Empty);
        }

        return configuration.GetEntry(path) is not null
            ? new BindingNode(configuration, path)
            : default;
    }

    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    private static object? BindInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        object? instance,
        BindingNode source,
        ConfigurationBinderOptions options)
    {
        if (!source.IsDefined)
        {
            return instance;
        }

        if (TryResolveConfigurationType(type, source, out object? configurationType))
        {
            return configurationType;
        }

        string? configValue = source.Value;

        if (configValue is not null &&
            TryConvertValue(type, configValue, source.Path, out object? convertedValue, out Exception? conversionError))
        {
            if (conversionError is not null)
            {
                throw conversionError;
            }

            return convertedValue;
        }

        BindingNode[] children = source.GetChildren();

        if (children.Length == 0)
        {
            return instance;
        }

        if (instance is null)
        {
            if (AttemptBindToCollectionInterfaces(type, children, options) is object collectionInstance)
            {
                return collectionInstance;
            }

            instance = CreateInstance(type);
        }

        Type? dictionaryInterface = FindDictionaryInterface(type) ?? FindDictionaryInterface(instance.GetType());

        if (dictionaryInterface is not null)
        {
            BindDictionary(instance, dictionaryInterface, children, options);
            return instance;
        }

        if (type.IsArray && instance is Array array)
        {
            return BindArray(array, children, options);
        }

        Type? collectionInterface = FindCollectionInterface(type) ?? FindCollectionInterface(instance.GetType());

        if (collectionInterface is not null)
        {
            BindCollection(instance, collectionInterface, children, options);
            return instance;
        }

        BindNonScalar(source, children, instance, options);

        return instance;
    }

    private static bool TryResolveConfigurationType(
        Type type,
        BindingNode source,
        [NotNullWhen(true)] out object? result)
    {
        IConfigurationEntry? entry = source.ResolveEntry();

        if (type == typeof(IConfiguration))
        {
            result = source.ResolveConfigurationRoot();
            return result is not null;
        }

        if (type == typeof(IConfigurationEntry))
        {
            result = entry;
            return result is not null;
        }

        if (type == typeof(IConfigurationSection))
        {
            result = entry as IConfigurationSection;
            return result is not null;
        }

        if (type == typeof(IConfigurationValue))
        {
            result = entry as IConfigurationValue;
            return result is not null;
        }

        result = null;
        return false;
    }

    [RequiresUnreferencedCode(PropertyTrimmingWarningMessage)]
    private static void BindNonScalar(
        BindingNode source,
        BindingNode[] children,
        object instance,
        ConfigurationBinderOptions options)
    {
        List<PropertyInfo> modelProperties = GetAllProperties(instance.GetType());

        if (options.ErrorOnUnknownConfiguration)
        {
            var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PropertyInfo property in modelProperties)
            {
                propertyNames.Add(property.Name);
            }

            var missingPropertyNames = new List<string>();

            foreach (BindingNode child in children)
            {
                string key = child.Key;

                if (!propertyNames.Contains(key))
                {
                    missingPropertyNames.Add($"'{key}'");
                }
            }

            if (missingPropertyNames.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The configuration contains keys that do not map to bindable properties on '{instance.GetType().FullName}': {string.Join(", ", missingPropertyNames)}.");
            }
        }

        foreach (PropertyInfo property in modelProperties)
        {
            BindProperty(property, instance, source, options);
        }
    }

    [RequiresUnreferencedCode(PropertyTrimmingWarningMessage)]
    private static void BindProperty(
        PropertyInfo property,
        object instance,
        BindingNode source,
        ConfigurationBinderOptions options)
    {
        if (property.GetMethod is null ||
            (!options.BindNonPublicProperties && !property.GetMethod.IsPublic) ||
            property.GetMethod.GetParameters().Length > 0)
        {
            return;
        }

        object? propertyValue = property.GetValue(instance);
        bool hasSetter = property.SetMethod is not null && (property.SetMethod.IsPublic || options.BindNonPublicProperties);

        if (propertyValue is null && !hasSetter)
        {
            return;
        }

        if (!source.TryGetChild(property.Name, out BindingNode childSource))
        {
            return;
        }

        Type bindingType = ResolvePropertyBindingType(property);

        if (propertyValue is not null && !bindingType.IsAssignableFrom(propertyValue.GetType()))
        {
            propertyValue = null;
        }

        object? boundValue = BindInstance(bindingType, propertyValue, childSource, options);

        if (boundValue is not null && hasSetter)
        {
            property.SetValue(instance, boundValue);
        }
    }

    [RequiresUnreferencedCode("Collection element types are reflected at runtime and cannot be fully analyzed by the trimmer.")]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    private static object? AttemptBindToCollectionInterfaces(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        BindingNode[] children,
        ConfigurationBinderOptions options)
    {
        if (!type.IsInterface)
        {
            return null;
        }

        Type? dictionaryInterface = FindDictionaryInterface(type);

        if (dictionaryInterface is not null)
        {
            Type keyType = dictionaryInterface.GenericTypeArguments[0];
            Type valueType = dictionaryInterface.GenericTypeArguments[1];
            Type dictionaryType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
            object dictionary = Activator.CreateInstance(dictionaryType)!;

            BindDictionary(dictionary, dictionaryInterface, children, options);

            return dictionary;
        }

        Type? collectionInterface = FindCollectionInterface(type);

        if (collectionInterface is not null)
        {
            Type itemType = collectionInterface.GenericTypeArguments[0];
            Type listType = typeof(List<>).MakeGenericType(itemType);
            object collection = Activator.CreateInstance(listType)!;

            BindCollection(collection, collectionInterface, children, options);

            return collection;
        }

        return null;
    }

    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    private static object CreateInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        Type type)
    {
        if (type.IsInterface || type.IsAbstract)
        {
            throw new InvalidOperationException(
                $"Cannot create an instance of abstract or interface type '{type.FullName}'. Use {nameof(ConfigurationBindingAttribute)} to declare a concrete binding type.");
        }

        if (type.IsArray)
        {
            if (type.GetArrayRank() > 1)
            {
                throw new InvalidOperationException($"Multidimensional arrays are not supported for configuration binding: '{type.FullName}'.");
            }

            Type? elementType = type.GetElementType();

            if (elementType is null)
            {
                throw new InvalidOperationException($"Unable to determine the array element type for '{type.FullName}'.");
            }

            return Array.CreateInstance(elementType, 0);
        }

        if (!type.IsValueType && type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"The type '{type.FullName}' must declare a public parameterless constructor to be created by the configuration binder.");
        }

        try
        {
            return Activator.CreateInstance(type)!;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Failed to activate an instance of '{type.FullName}' for configuration binding.",
                exception);
        }
    }

    [RequiresUnreferencedCode("Dictionary value types are reflected at runtime and cannot be fully analyzed by the trimmer.")]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    private static void BindDictionary(
        object dictionary,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties)] Type dictionaryType,
        BindingNode[] children,
        ConfigurationBinderOptions options)
    {
        Type keyType = dictionaryType.GenericTypeArguments[0];
        Type valueType = dictionaryType.GenericTypeArguments[1];
        bool keyTypeIsEnum = keyType.IsEnum;

        if (keyType != typeof(string) && !keyTypeIsEnum)
        {
            return;
        }

        PropertyInfo? itemProperty = dictionary.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (itemProperty is null)
        {
            return;
        }

        foreach (BindingNode child in children)
        {
            object? item = BindInstance(valueType, instance: null, child, options);

            if (item is null)
            {
                continue;
            }

            object key = keyType == typeof(string)
                ? (string)child.Key
                : Enum.Parse(keyType, child.Key, ignoreCase: true);

            itemProperty.SetValue(dictionary, item, [key]);
        }
    }

    [RequiresUnreferencedCode("Collection element types are reflected at runtime and cannot be fully analyzed by the trimmer.")]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    private static void BindCollection(
        object collection,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type collectionType,
        BindingNode[] children,
        ConfigurationBinderOptions options)
    {
        Type itemType = collectionType.GenericTypeArguments[0];
        MethodInfo? addMethod = collection.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, binder: null, [itemType], modifiers: null) ??
                                collectionType.GetMethod("Add", bindingLookup);

        if (addMethod is null)
        {
            return;
        }

        foreach (BindingNode child in children)
        {
            object? item = BindInstance(itemType, instance: null, child, options);

            if (item is null)
            {
                continue;
            }

            addMethod.Invoke(collection, [item]);
        }
    }

    [RequiresUnreferencedCode("Array element types are reflected at runtime and cannot be fully analyzed by the trimmer.")]
    [RequiresDynamicCode(DynamicCodeWarningMessage)]
    private static Array BindArray(Array source, BindingNode[] children, ConfigurationBinderOptions options)
    {
        Type? elementType = source.GetType().GetElementType();

        if (elementType is null)
        {
            throw new InvalidOperationException($"Unable to determine the array element type for '{source.GetType().FullName}'.");
        }

        var items = new List<object?>(source.Length + children.Length);

        foreach (object? item in source)
        {
            items.Add(item);
        }

        foreach (BindingNode child in children)
        {
            object? item = BindInstance(elementType, instance: null, child, options);

            if (item is not null)
            {
                items.Add(item);
            }
        }

        Array array = Array.CreateInstance(elementType, items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            array.SetValue(items[i], i);
        }

        return array;
    }

    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    private static bool TryConvertValue(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        string value,
        Path path,
        out object? result,
        out Exception? error)
    {
        error = null;
        result = null;

        if (type == typeof(string) || type == typeof(object))
        {
            result = value;
            return true;
        }

        if (type == typeof(Key))
        {
            result = new Key(value);
            return true;
        }

        if (type == typeof(Path))
        {
            result = Path.Parse(value);
            return true;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            return TryConvertValue(Nullable.GetUnderlyingType(type)!, value, path, out result, out error);
        }

        TypeConverter converter = TypeDescriptor.GetConverter(type);

        if (converter.CanConvertFrom(typeof(string)))
        {
            try
            {
                result = converter.ConvertFromInvariantString(value);
            }
            catch (Exception exception)
            {
                error = new InvalidOperationException(
                    $"Failed to bind the configuration value at '{FormatPath(path)}' to type '{type.FullName}'.",
                    exception);
            }

            return true;
        }

        if (type.IsEnum)
        {
            try
            {
                result = Enum.Parse(type, value, ignoreCase: true);
            }
            catch (Exception exception)
            {
                error = new InvalidOperationException(
                    $"Failed to bind the configuration value at '{FormatPath(path)}' to type '{type.FullName}'.",
                    exception);
            }

            return true;
        }

        if (type == typeof(byte[]))
        {
            try
            {
                result = Convert.FromBase64String(value);
            }
            catch (FormatException exception)
            {
                error = new InvalidOperationException(
                    $"Failed to bind the configuration value at '{FormatPath(path)}' to type '{type.FullName}'.",
                    exception);
            }

            return true;
        }

        return false;
    }

    [RequiresUnreferencedCode(TrimmingWarningMessage)]
    private static object ConvertValue(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        string value,
        Path path)
    {
        bool convertible = TryConvertValue(type, value, path, out object? result, out Exception? error);

        if (!convertible)
        {
            throw new InvalidCastException($"The configuration value at '{FormatPath(path)}' cannot be converted to '{type.FullName}'.");
        }

        if (error is not null)
        {
            throw error;
        }

        return result!;
    }

    private static Type? FindOpenGenericInterface(
        Type expected,
        Type actual)
    {
        if (actual.IsGenericType && actual.GetGenericTypeDefinition() == expected)
        {
            return actual;
        }

        Type[] interfaces = actual.GetInterfaces();

        for (int i = 0; i < interfaces.Length; i++)
        {
            Type interfaceType = interfaces[i];

            if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == expected)
            {
                return interfaceType;
            }
        }

        return null;
    }

    private static Type? FindDictionaryInterface(Type type)
    {
        return FindOpenGenericInterface(typeof(IDictionary<,>), type) ??
               FindOpenGenericInterface(typeof(IReadOnlyDictionary<,>), type);
    }

    private static Type? FindCollectionInterface(Type type)
    {
        return FindOpenGenericInterface(typeof(ICollection<>), type) ??
               FindOpenGenericInterface(typeof(IList<>), type) ??
               FindOpenGenericInterface(typeof(IReadOnlyList<>), type) ??
               FindOpenGenericInterface(typeof(IReadOnlyCollection<>), type) ??
               FindOpenGenericInterface(typeof(IEnumerable<>), type);
    }

    private static List<PropertyInfo> GetAllProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type)
    {
        var allProperties = new List<PropertyInfo>();

        while (type != typeof(object) && type is not null)
        {
            allProperties.AddRange(type.GetProperties(bindingLookup));
            type = type.BaseType!;
        }

        return allProperties;
    }

    private static Type ResolvePropertyBindingType(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<ConfigurationBindingAttribute>(inherit: true);

        if (attribute is null)
        {
            return property.PropertyType;
        }

        if (!property.PropertyType.IsAssignableFrom(attribute.Type))
        {
            throw new InvalidOperationException(
                $"The binding type '{attribute.Type.FullName}' is not assignable to property '{property.DeclaringType?.FullName}.{property.Name}'.");
        }

        return attribute.Type;
    }

    private static string FormatPath(Path path)
    {
        return path.IsEmpty
            ? "<root>"
            : path.ToString();
    }

    private static IEnumerable<IConfigurationEntry> GetProviderChildren(IConfigurationProvider provider, Path path)
    {
        if (path.IsEmpty)
        {
            return provider.GetEntries();
        }

        return provider.GetEntry(path) is IConfigurationSection section
            ? section.GetChildren()
            : [];
    }

    private static BindingNode[] CreateEntryChildren(IEnumerable<IConfigurationEntry> entries)
    {
        var children = new List<BindingNode>();

        foreach (IConfigurationEntry entry in entries)
        {
            children.Add(new BindingNode(entry));
        }

        children.Sort(BindingNodeComparer.Instance);

        return [.. children];
    }

    private static BindingNode[] GetMergedChildren(IConfiguration configuration, Path path)
    {
        IConfigurationProvider[] providers = [.. configuration.Providers];
        var children = new List<BindingNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = providers.Length - 1; i >= 0; i--)
        {
            foreach (IConfigurationEntry child in GetProviderChildren(providers[i], path))
            {
                string key = child.Key;

                if (!seen.Add(key))
                {
                    continue;
                }

                if (configuration.GetEntry(child.Path) is not null)
                {
                    children.Add(new BindingNode(configuration, child.Path));
                }
            }
        }

        children.Sort(BindingNodeComparer.Instance);

        return [.. children];
    }

    private sealed class BindingNodeComparer : IComparer<BindingNode>
    {
        public static readonly BindingNodeComparer Instance = new();

        public int Compare(BindingNode left, BindingNode right)
        {
            string leftKey = left.Key;
            string rightKey = right.Key;

            bool leftNumeric = int.TryParse(leftKey, out int leftIndex);
            bool rightNumeric = int.TryParse(rightKey, out int rightIndex);

            if (leftNumeric && rightNumeric)
            {
                return leftIndex.CompareTo(rightIndex);
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(leftKey, rightKey);
        }
    }

    private readonly struct BindingNode
    {
        private readonly IConfiguration? _configuration;
        private readonly IConfigurationEntry? _entry;

        public BindingNode(IConfiguration configuration, Path path)
        {
            _configuration = configuration;
            _entry = null;
            Path = path;
            IsDefined = true;
        }

        public BindingNode(IConfigurationEntry entry)
        {
            _configuration = null;
            _entry = entry;
            Path = entry.Path;
            IsDefined = true;
        }

        public bool IsDefined { get; }

        public Path Path { get; }

        public Key Key => Path.IsEmpty ? default : Path[Path.Count - 1];

        public string? Value => ResolveEntry() is IConfigurationValue value ? value.Value : null;

        public IConfiguration? ResolveConfigurationRoot() => _configuration;

        public IConfigurationEntry? ResolveEntry()
        {
            if (!IsDefined)
            {
                return null;
            }

            if (_entry is not null)
            {
                return _entry;
            }

            if (_configuration is null || Path.IsEmpty)
            {
                return null;
            }

            return _configuration.GetEntry(Path);
        }

        public BindingNode[] GetChildren()
        {
            if (!IsDefined)
            {
                return [];
            }

            if (_configuration is not null)
            {
                return GetMergedChildren(_configuration, Path);
            }

            if (_entry is IConfigurationSection section)
            {
                return CreateEntryChildren(section.GetChildren());
            }

            return [];
        }

        public bool TryGetChild(string key, out BindingNode child)
        {
            ArgumentNullException.ThrowIfNull(key);

            if (!IsDefined)
            {
                child = default;
                return false;
            }

            Path childPath = Path.IsEmpty
                ? (Path)key
                : Path.Combine(Path, (Path)key);

            if (_configuration is not null)
            {
                if (_configuration.GetEntry(childPath) is null)
                {
                    child = default;
                    return false;
                }

                child = new BindingNode(_configuration, childPath);
                return true;
            }

            if (_entry is IConfigurationSection section && section.GetEntry((Path)key) is IConfigurationEntry childEntry)
            {
                child = new BindingNode(childEntry);
                return true;
            }

            child = default;
            return false;
        }
    }

    private sealed class MissingValue
    {
        public static readonly MissingValue Instance = new();

        private MissingValue()
        {
        }
    }
}
