using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class WebApplicationConfigurationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Configuration: Local and Development load distinct environment files")]
    [InlineData(AppEnvironment.Keys.Local, "local-machine")]
    [InlineData(AppEnvironment.Keys.Development, "development-deployment")]
    public void CreateBuilderWithArgs_WithLocalOrDevelopmentEnvironment_ShouldLoadMatchingFile(
        string environmentName,
        string expectedValue)
    {
        // Arrange
        string contentRootPath = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-web-configuration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRootPath);
        try
        {
            File.WriteAllText(Path.Combine(contentRootPath, "appsettings.json"),
                """{"EnvironmentFileSelection":"base"}""");
            File.WriteAllText(Path.Combine(contentRootPath, "appsettings.Local.json"),
                """{"EnvironmentFileSelection":"local-machine"}""");
            File.WriteAllText(Path.Combine(contentRootPath, "appsettings.Development.json"),
                """{"EnvironmentFileSelection":"development-deployment"}""");
            using IDisposable resourceScope = ResourceRuntime.CreateScope(new ResourceContext(
                environmentName: environmentName,
                contentRootPath: contentRootPath));

            // Act
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
                [],
                typeof(WebApplicationConfigurationTests).Assembly);
            try
            {
                // Assert
                builder.Configuration["EnvironmentFileSelection"].ShouldBe(expectedValue);
            }
            finally
            {
                builder.Configuration.Dispose();
            }
        }
        finally
        {
            Directory.Delete(contentRootPath, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(args): composes the default configuration sources in precedence order")]
    public void CreateBuilderWithArgs_WhenConfigurationSourcesOverlap_ShouldApplyDocumentedPrecedence()
    {
        string contentRootPath = Path.Combine(
            Path.GetTempPath(),
            $"cohesion-web-configuration-{Guid.NewGuid():N}");
        string environmentName = $"Configuration-{Guid.NewGuid():N}";
        Directory.CreateDirectory(contentRootPath);
        try
        {
            // Arrange
            File.WriteAllText(
                Path.Combine(contentRootPath, "appsettings.json"),
                """
                {
                  "Settings": {
                    "BaseOnly": "base",
                    "EnvironmentFile": "base",
                    "EnvironmentVariable": "base",
                    "Ambient": "base",
                    "Arguments": "base",
                    "Winner": "base"
                  }
                }
                """);
            File.WriteAllText(
                Path.Combine(contentRootPath, $"appsettings.{environmentName}.json"),
                """
                {
                  "Settings": {
                    "EnvironmentFile": "environment-file",
                    "EnvironmentVariable": "environment-file",
                    "Ambient": "environment-file",
                    "Arguments": "environment-file",
                    "Winner": "environment-file"
                  }
                }
                """);

            using var variables = new EnvironmentVariablesScope(
                ("COHESION_CONFIG__Settings__EnvironmentVariable", "environment-variable"),
                ("COHESION_CONFIG__Settings__Ambient", "environment-variable"),
                ("COHESION_CONFIG__Settings__Arguments", "environment-variable"),
                ("COHESION_CONFIG__Settings__Winner", "environment-variable"));
            using IDisposable resourceScope = ResourceRuntime.CreateScope(new ResourceContext(
                environmentName: environmentName,
                contentRootPath: contentRootPath,
                settings: new Dictionary<string, string>
                {
                    ["Settings:Ambient"] = "ambient",
                    ["Settings:Arguments"] = "ambient",
                    ["Settings:Winner"] = "ambient",
                }));

            // Act
            WebApplicationBuilder builder = WebApplication.CreateBuilder(
            [
                "--Settings:Arguments=arguments",
                "--Settings:Winner=arguments",
            ],
            typeof(WebApplicationConfigurationTests).Assembly);

            try
            {
                // Assert
                builder.Configuration["Settings:BaseOnly"].ShouldBe("base");
                builder.Configuration["Settings:EnvironmentFile"].ShouldBe("environment-file");
                builder.Configuration["Settings:EnvironmentVariable"].ShouldBe("environment-variable");
                builder.Configuration["Settings:Ambient"].ShouldBe("ambient");
                builder.Configuration["Settings:Arguments"].ShouldBe("arguments");
                builder.Configuration["Settings:Winner"].ShouldBe("arguments");
            }
            finally
            {
                builder.Configuration.Dispose();
            }
        }
        finally
        {
            Directory.Delete(contentRootPath, recursive: true);
        }
    }

    private sealed class EnvironmentVariablesScope : IDisposable
    {
        private readonly List<(string Key, string? Value)> _originalValues = new();

        public EnvironmentVariablesScope(params (string Key, string Value)[] values)
        {
            foreach ((string key, string value) in values)
            {
                _originalValues.Add((
                    key,
                    Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process)));
                Environment.SetEnvironmentVariable(
                    key,
                    value,
                    EnvironmentVariableTarget.Process);
            }
        }

        public void Dispose()
        {
            foreach ((string key, string? value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(
                    key,
                    value,
                    EnvironmentVariableTarget.Process);
            }
        }
    }
}
