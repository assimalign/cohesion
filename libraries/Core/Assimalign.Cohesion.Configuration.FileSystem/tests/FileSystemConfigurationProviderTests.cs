using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.FileSystem.Tests;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.FileSystem;
using Assimalign.Cohesion.FileSystem;

public class FileSystemConfigurationProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration.FileSystem] - Provider: Required missing file throws")]
    public async Task Provider_RequiredMissingFile_ShouldThrow()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        var provider = new TestTextConfigurationProvider(new FileSystemConfigurationOptions
        {
            FileSystem = fileSystem,
            Path = "settings.txt",
        });

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.LoadAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.FileSystem] - Provider: Optional missing file leaves provider empty")]
    public async Task Provider_OptionalMissingFile_ShouldRemainEmpty()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        var provider = new TestTextConfigurationProvider(new FileSystemConfigurationOptions
        {
            FileSystem = fileSystem,
            Path = "settings.txt",
            Optional = true,
        });

        await provider.LoadAsync();

        Assert.Empty(provider.GetEntries());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.FileSystem] - Provider: Existing file loads key values")]
    public async Task Provider_ExistingFile_ShouldLoadValues()
    {
        using IFileSystem fileSystem = CreateFileSystem();
        WriteFile(fileSystem, "settings.txt", """
            Feature:Enabled=true
            Logging:Level=Debug
            """);

        var provider = new TestTextConfigurationProvider(new FileSystemConfigurationOptions
        {
            FileSystem = fileSystem,
            Path = "settings.txt",
        });

        await provider.LoadAsync();

        Assert.Equal("true", GetValue(provider, "Feature:Enabled"));
        Assert.Equal("Debug", GetValue(provider, "Logging:Level"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration.FileSystem] - Provider: Reload updates values when the file changes")]
    public async Task Provider_ReloadOnChange_ShouldRefreshValues()
    {
        string rootPath = global::System.IO.Path.Combine(
            global::System.IO.Path.GetTempPath(),
            $"cohesion-configfs-{Guid.NewGuid():N}");

        Directory.CreateDirectory(rootPath);

        try
        {
            using IFileSystem fileSystem = new PhysicalFileSystem(rootPath);
            WriteFile(fileSystem, "settings.txt", "Mode=Initial");

            var provider = new TestTextConfigurationProvider(new FileSystemConfigurationOptions
            {
                FileSystem = fileSystem,
                Path = "settings.txt",
                ReloadOnChange = true,
                ReloadDelay = TimeSpan.FromMilliseconds(100),
            });

            await provider.LoadAsync();
            WriteFile(fileSystem, "settings.txt", "Mode=Updated");

            await WaitForConditionAsync(
                static state => GetValue(state, "Mode") == "Updated",
                provider,
                TimeSpan.FromSeconds(5));

            Assert.Equal("Updated", GetValue(provider, "Mode"));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
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
        ArgumentNullException.ThrowIfNull(fileSystem);

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

    private static async Task WaitForConditionAsync<TState>(
        Func<TState, bool> predicate,
        TState state,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (predicate(state))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(state), "The expected condition was not satisfied before the timeout elapsed.");
    }

    private static string? GetValue(IConfigurationProvider provider, Path path)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.TryGet(path, out string? value) ? value : null;
    }

    private sealed class TestTextConfigurationProvider : FileSystemConfigurationProvider
    {
        private readonly string _name;

        public TestTextConfigurationProvider(FileSystemConfigurationOptions options)
            : base(options)
        {
            _name = $"{nameof(TestTextConfigurationProvider)}[{options.Path}]";
        }

        public override string Name => _name;

        protected override Task ReadAsync(
            Stream stream,
            IDictionary<Path, string?> entries,
            CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new FormatException($"The test configuration line '{line}' is invalid.");
                }

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                entries[Path.Parse(key)] = value;
            }

            return Task.CompletedTask;
        }
    }
}
