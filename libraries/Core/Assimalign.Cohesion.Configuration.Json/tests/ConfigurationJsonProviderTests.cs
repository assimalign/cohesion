using System;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Configuration.Json.Tests;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.Json;
using Assimalign.Cohesion.FileSystem;

public class ConfigurationJsonProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration.Json] - Builder: Json file loads nested keys")]
    public void Builder_AddJsonFile_ShouldLoadNestedKeys()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        WriteFile(fileSystem, "settings.json", """
            {
              "Logging": {
                "Level": "Debug",
                "Enabled": true
              },
              "Servers": [
                "one",
                "two"
              ]
            }
            """);

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddJsonFile(fileSystem, "settings.json")
            .Build();

        try
        {
            Assert.Equal("Debug", configuration["Logging:Level"]);
            Assert.Equal(bool.TrueString, configuration["Logging:Enabled"]);
            Assert.Equal("one", configuration["Servers:0"]);
            Assert.Equal("two", configuration["Servers:1"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Json] - Builder: Json stream loads values")]
    public void Builder_AddJsonStream_ShouldLoadValues()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {
              "Feature": {
                "Mode": "Live"
              }
            }
            """));

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddJsonStream(stream, leaveOpen: true)
            .Build();

        try
        {
            Assert.Equal("Live", configuration["Feature:Mode"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Json] - Builder: Optional missing file is ignored")]
    public void Builder_AddJsonFile_OptionalMissingFile_ShouldBeIgnored()
    {
        using IFileSystem fileSystem = CreateFileSystem();

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddJsonFile(fileSystem, "missing.json", optional: true)
            .Build();

        try
        {
            Assert.Null(configuration["Feature:Mode"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    private static IFileSystem CreateFileSystem()
    {
        return new InMemoryFileSystem(new InMemoryFileSystemOptions
        {
            RootPath = "/",
            Size = Size.FromMegabytes(8),
        });
    }

    private static void WriteFile(IFileSystem fileSystem, FileSystemPath path, string content)
    {
        if (!fileSystem.Exists(path))
        {
            fileSystem.CreateFile(path);
        }

        IFileSystemFile file = fileSystem.GetFile(path);

        using Stream stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(0);

        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
