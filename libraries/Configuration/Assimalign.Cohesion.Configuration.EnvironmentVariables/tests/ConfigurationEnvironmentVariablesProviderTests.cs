using System;
using System.Collections.Generic;
using Xunit;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationEnvironmentVariablesProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Environment Variables] - Builder: AddEnvironmentVariables loads prefixed values")]
    public void EnvironmentVariables_AddEnvironmentVariables_ShouldLoadPrefixedValues()
    {
        string prefix = $"COHESION_ENV_{CreateToken()}_";

        using var variables = new EnvironmentVariablesScope(
            ($"{prefix}Logging__Level", "Debug"),
            ($"{prefix}Features__UseCache", "true"));

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix)
            .Build();

        Assert.Equal("Debug", configuration["Logging:Level"]);
        Assert.Equal("true", configuration["Features:UseCache"]);
    }

    [Fact(DisplayName = "Cohesion Test [Environment Variables] - Builder: Prefix is trimmed before key normalization")]
    public void EnvironmentVariables_AddEnvironmentVariables_ShouldTrimPrefixBeforeNormalizingKey()
    {
        string prefix = $"COHESION__{CreateToken()}__";

        using var variables = new EnvironmentVariablesScope(
            ($"{prefix}Nested__Value", "42"));

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix)
            .Build();

        Assert.Equal("42", configuration["Nested:Value"]);
    }

    [Fact(DisplayName = "Cohesion Test [Environment Variables] - Builder: Configure callback uses options prefix")]
    public void EnvironmentVariables_AddEnvironmentVariables_WithConfigureOptions_ShouldUsePrefix()
    {
        string prefix = $"COHESION_ENV_{CreateToken()}_";

        using var variables = new EnvironmentVariablesScope(
            ($"{prefix}Service__Url", "https://example.test"));

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(options => options.Prefix = prefix)
            .Build();

        Assert.Equal("https://example.test", configuration["Service:Url"]);
    }

    [Fact(DisplayName = "Cohesion Test [Environment Variables] - Provider: Connection string prefixes are mapped")]
    public void EnvironmentVariables_Provider_ShouldMapConnectionStringPrefixes()
    {
        string token = CreateToken();

        using var variables = new EnvironmentVariablesScope(
            ($"MYSQLCONNSTR_{token}", "Server=db;Uid=user;Pwd=pass;"));

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        Assert.Equal("Server=db;Uid=user;Pwd=pass;", configuration[$"ConnectionStrings:{token}"]);
        Assert.Equal("MySql.Data.MySqlClient", configuration[$"ConnectionStrings:{token}_ProviderName"]);
    }

    private static string CreateToken()
    {
        return Guid.NewGuid().ToString("N").ToUpperInvariant();
    }

    private sealed class EnvironmentVariablesScope : IDisposable
    {
        private readonly List<(string Key, string? PreviousValue)> _values;

        public EnvironmentVariablesScope(params (string Key, string Value)[] values)
        {
            _values = [];

            foreach ((string key, string value) in values)
            {
                _values.Add((key, Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process)));
                Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
            }
        }

        public void Dispose()
        {
            foreach ((string key, string? previousValue) in _values)
            {
                Environment.SetEnvironmentVariable(key, previousValue, EnvironmentVariableTarget.Process);
            }
        }
    }
}
