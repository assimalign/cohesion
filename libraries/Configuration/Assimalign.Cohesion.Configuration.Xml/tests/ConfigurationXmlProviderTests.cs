using System;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Configuration.Xml.Tests;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.Xml;
using Assimalign.Cohesion.FileSystem;

public class ConfigurationXmlProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration.Xml] - Builder: Xml file loads nested keys")]
    public void Builder_AddXmlFile_ShouldLoadNestedKeys()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        WriteFile(fileSystem, "settings.xml", """
            <settings>
              <logging>
                <level>Debug</level>
              </logging>
              <servers>
                <server>one</server>
                <server>two</server>
              </servers>
            </settings>
            """);

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddXmlFile(fileSystem, "settings.xml")
            .Build();

        try
        {
            Assert.Equal("Debug", configuration["logging:level"]);
            Assert.Equal("one", configuration["servers:server:0"]);
            Assert.Equal("two", configuration["servers:server:1"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Xml] - Builder: Xml stream honors Name attribute keys")]
    public void Builder_AddXmlStream_ShouldLoadNameAttributeKeys()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <settings>
              <connections>
                <add Name="Main" provider="SqlClient">
                  <value>Data Source=db</value>
                </add>
              </connections>
            </settings>
            """));

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddXmlStream(stream, leaveOpen: true)
            .Build();

        try
        {
            Assert.Equal("Data Source=db", configuration["connections:add:Main:value"]);
            Assert.Equal("SqlClient", configuration["connections:add:Main:provider"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Xml] - Builder: Optional missing file is ignored")]
    public void Builder_AddXmlFile_OptionalMissingFile_ShouldBeIgnored()
    {
        using IFileSystem fileSystem = CreateFileSystem();

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddXmlFile(fileSystem, "missing.xml", optional: true)
            .Build();

        try
        {
            Assert.Null(configuration["connections:add:Main"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Xml] - Provider: Mixed text and attributes throws")]
    public void Provider_MixedTextAndAttributes_ShouldThrow()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <settings>
              <connections>
                <add Name="Main" provider="SqlClient">Data Source=db</add>
              </connections>
            </settings>
            """));

        var provider = new ConfigurationXmlStreamProvider(stream, leaveOpen: true);

        FormatException exception = Assert.Throws<FormatException>(() => provider.Load());

        Assert.Contains("cannot contain both direct text content", exception.Message, StringComparison.Ordinal);
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
