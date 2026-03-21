using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Loads configuration values from process environment variables.
/// </summary>
public class ConfigurationEnvironmentVariablesProvider : ConfigurationProvider
{
    private const string CustomConnectionStringPrefix = "CUSTOMCONNSTR_";
    private const string DoubleUnderscore = "__";
    private const string MySqlConnectionStringPrefix = "MYSQLCONNSTR_";
    private const string NormalizedDelimiter = ":";
    private const string SqlAzureConnectionStringPrefix = "SQLAZURECONNSTR_";
    private const string SqlConnectionStringPrefix = "SQLCONNSTR_";

    private readonly string _name;
    private readonly string _prefix;

    /// <summary>
    /// Creates an environment variables provider without a prefix filter.
    /// </summary>
    public ConfigurationEnvironmentVariablesProvider() : this(string.Empty)
    {
    }

    /// <summary>
    /// Creates an environment variables provider with an optional prefix filter.
    /// </summary>
    /// <param name="prefix">The prefix that environment variable names must start with.</param>
    public ConfigurationEnvironmentVariablesProvider(string? prefix)
    {
        _prefix = prefix ?? string.Empty;
        _name = string.IsNullOrEmpty(_prefix)
            ? nameof(ConfigurationEnvironmentVariablesProvider)
            : $"{nameof(ConfigurationEnvironmentVariablesProvider)}[{_prefix}]";
    }

    /// <inheritdoc />
    public override string Name => _name;

    /// <inheritdoc />
    protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
    {
        Load(Environment.GetEnvironmentVariables(), entries, _prefix, cancellationToken);
        return Task.CompletedTask;
    }

    internal static void Load(
        IDictionary environmentVariables,
        IDictionary<Path, string?> entries,
        string? prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        ArgumentNullException.ThrowIfNull(entries);

        prefix ??= string.Empty;

        foreach (DictionaryEntry entry in environmentVariables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Key is not string key)
            {
                continue;
            }

            string? value = entry.Value as string ?? entry.Value?.ToString();

            if (value is null)
            {
                continue;
            }

            if (TryAddConnectionStringEntry(entries, prefix, key, value))
            {
                continue;
            }

            TryAddPrefixedEntry(entries, prefix, key, value);
        }
    }

    private static bool TryAddConnectionStringEntry(
        IDictionary<Path, string?> entries,
        string prefix,
        string key,
        string value)
    {
        string? connectionStringPrefix = null;
        string? providerName = null;

        if (key.StartsWith(MySqlConnectionStringPrefix, StringComparison.OrdinalIgnoreCase))
        {
            connectionStringPrefix = MySqlConnectionStringPrefix;
            providerName = "MySql.Data.MySqlClient";
        }
        else if (key.StartsWith(SqlAzureConnectionStringPrefix, StringComparison.OrdinalIgnoreCase))
        {
            connectionStringPrefix = SqlAzureConnectionStringPrefix;
            providerName = "System.Data.SqlClient";
        }
        else if (key.StartsWith(SqlConnectionStringPrefix, StringComparison.OrdinalIgnoreCase))
        {
            connectionStringPrefix = SqlConnectionStringPrefix;
            providerName = "System.Data.SqlClient";
        }
        else if (key.StartsWith(CustomConnectionStringPrefix, StringComparison.OrdinalIgnoreCase))
        {
            connectionStringPrefix = CustomConnectionStringPrefix;
        }

        if (connectionStringPrefix is null)
        {
            return false;
        }

        string normalizedKey = NormalizeKey(key.AsSpan(connectionStringPrefix.Length));

        if (string.IsNullOrEmpty(normalizedKey))
        {
            return true;
        }

        AddIfPrefixed(entries, prefix, $"ConnectionStrings:{normalizedKey}", value);

        if (providerName is not null)
        {
            AddIfPrefixed(entries, prefix, $"ConnectionStrings:{normalizedKey}_ProviderName", providerName);
        }

        return true;
    }

    private static bool TryAddPrefixedEntry(
        IDictionary<Path, string?> entries,
        string prefix,
        string key,
        string value)
    {
        if (!key.AsSpan().StartsWith(prefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string normalizedKey = NormalizeKey(key.AsSpan(prefix.Length));

        if (string.IsNullOrEmpty(normalizedKey))
        {
            return false;
        }

        entries[Path.Parse(normalizedKey)] = value;

        return true;
    }

    private static void AddIfPrefixed(
        IDictionary<Path, string?> entries,
        string prefix,
        string key,
        string value)
    {
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReadOnlySpan<char> trimmedKey = key.AsSpan(prefix.Length);

        if (trimmedKey.IsEmpty)
        {
            return;
        }

        entries[Path.Parse(trimmedKey)] = value;
    }

    private static string NormalizeKey(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty)
        {
            return string.Empty;
        }

        string value = key.ToString();

        return value.IndexOf(DoubleUnderscore, StringComparison.Ordinal) >= 0
            ? value.Replace(DoubleUnderscore, NormalizedDelimiter, StringComparison.Ordinal)
            : value;
    }
}
