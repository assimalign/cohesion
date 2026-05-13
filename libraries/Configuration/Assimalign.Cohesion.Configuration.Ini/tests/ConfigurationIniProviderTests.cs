using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Ini.Tests;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.FileSystem;
using Assimalign.Cohesion.Configuration.Ini;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Provider-level coverage. Exercises <see cref="ConfigurationIniProvider"/> through the
/// public <see cref="ConfigurationBuilder"/> + <see cref="ConfigurationBuilderExtensions"/> surface,
/// and validates lifecycle semantics inherited from <see cref="FileSystemConfigurationProvider"/>.
/// </summary>
public class ConfigurationIniProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: AddIniFile loads sections and root keys")]
    public void Builder_AddIniFile_LoadsSectionsAndRootKeys()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        WriteFile(fileSystem, "settings.ini", """
            Mode = Live

            [Logging]
            Level = Debug
            Enabled = true

            [Logging:Console]
            Level = Information
            """);

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniFile(fileSystem, "settings.ini")
            .Build();

        try
        {
            Assert.Equal("Live", configuration["Mode"]);
            Assert.Equal("Debug", configuration["Logging:Level"]);
            Assert.Equal("true", configuration["Logging:Enabled"]);
            Assert.Equal("Information", configuration["Logging:Console:Level"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: AddIniStream loads values")]
    public void Builder_AddIniStream_LoadsValues()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            [Feature]
            Mode = Live
            """));

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniStream(stream, leaveOpen: true)
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

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: optional missing file is ignored")]
    public void Builder_AddIniFile_OptionalMissingFile_IsIgnored()
    {
        using IFileSystem fileSystem = CreateFileSystem();

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniFile(fileSystem, "missing.ini", optional: true)
            .Build();

        try
        {
            Assert.Null(configuration["Anything"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: required missing file throws on build")]
    public void Builder_AddIniFile_RequiredMissingFile_Throws()
    {
        using IFileSystem fileSystem = CreateFileSystem();

        IConfigurationBuilder builder = new ConfigurationBuilder()
            .AddIniFile(fileSystem, "missing.ini", optional: false);

        // The provider load happens during Build(); a missing required file
        // must surface as an exception so misconfigured callers find out early.
        Assert.ThrowsAny<Exception>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: AddIniFile honors OnLoadException callback")]
    public void Builder_AddIniFile_OnLoadException_IsInvoked()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        // Malformed file -> the parser throws FormatException -> the callback decides.
        WriteFile(fileSystem, "broken.ini", "[unterminated");

        Exception? captured = null;

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniFile(options =>
            {
                options.FileSystem = fileSystem;
                options.Path = "broken.ini";
                options.OnLoadException = ctx =>
                {
                    captured = ctx.Exception;
                    ctx.Ignore = true;
                };
            })
            .Build();

        try
        {
            Assert.NotNull(captured);
            Assert.IsType<FormatException>(captured);
            // Provider remained usable with empty entries because Ignore=true.
            Assert.Null(configuration["anything"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: AddIniFile rejects empty path")]
    public void Builder_AddIniFile_EmptyPath_Throws()
    {
        using IFileSystem fileSystem = CreateFileSystem();

        Assert.Throws<ArgumentException>(() =>
            new ConfigurationBuilder().AddIniFile(fileSystem, FileSystemPath.Empty));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: AddIniFile rejects null arguments")]
    public void Builder_AddIniFile_NullArguments_Throw()
    {
        var builder = new ConfigurationBuilder();
        using IFileSystem fileSystem = CreateFileSystem();

        Assert.Throws<ArgumentNullException>(
            () => ((IConfigurationBuilder)null!).AddIniFile(fileSystem, "settings.ini"));
        Assert.Throws<ArgumentNullException>(
            () => builder.AddIniFile((IFileSystem)null!, "settings.ini"));
        Assert.Throws<ArgumentNullException>(
            () => builder.AddIniFile((Action<ConfigurationIniOptions>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - Builder: AddIniStream rejects null arguments")]
    public void Builder_AddIniStream_NullArguments_Throw()
    {
        var builder = new ConfigurationBuilder();
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentNullException>(
            () => ((IConfigurationBuilder)null!).AddIniStream(stream));
        Assert.Throws<ArgumentNullException>(
            () => builder.AddIniStream(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - StreamProvider: rewinds seekable stream before each load")]
    public void StreamProvider_SeekableStream_IsRewoundBeforeLoad()
    {
        // The stream has been positioned past the BOM/start; the provider must
        // rewind it so the entire content is read on (re)load.
        byte[] body = Encoding.UTF8.GetBytes("Key = Value");
        using var stream = new MemoryStream(body);
        stream.Position = body.Length;

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniStream(stream, leaveOpen: true)
            .Build();

        try
        {
            Assert.Equal("Value", configuration["Key"]);
        }
        finally
        {
            configuration.Dispose();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - StreamProvider: closes stream when leaveOpen=false on dispose")]
    public async Task StreamProvider_LeaveOpenFalse_DisposesStream()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("Key = Value"));

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniStream(stream, leaveOpen: false)
            .Build();

        await configuration.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - StreamProvider: keeps stream open when leaveOpen=true on dispose")]
    public async Task StreamProvider_LeaveOpenTrue_KeepsStreamOpen()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Key = Value"));

        Configuration configuration = (Configuration)new ConfigurationBuilder()
            .AddIniStream(stream, leaveOpen: true)
            .Build();

        await configuration.DisposeAsync();

        // No throw - the stream is still usable.
        Assert.True(stream.CanRead);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.Ini] - StreamProvider: constructor rejects null stream")]
    public void StreamProvider_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigurationIniStreamProvider(null!));
    }

    // ---- helpers ----

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
