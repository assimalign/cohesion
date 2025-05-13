using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.FileSystem;

public abstract class FileSystemConfigurationProvider : ConfigurationProvider
{
    private readonly IFileSystemFile _file;
    private readonly IChangeToken? _changeToken;
    private readonly IDisposable? _onChange;

    public FileSystemConfigurationProvider(FileSystemConfigurationOptions options)
    {
        var fileSystem = options.FileSystem;

        _file = fileSystem.GetFile(options.Path);
        _changeToken = _file.Watch();

        if (options.ReloadOnChange)
        {
            _onChange = _changeToken.OnChange(async state =>
            {
                var provider = (FileSystemConfigurationProvider)state!;

                await provider.ReloadAsync();

            }, this);
        }
    }


    public override void Dispose()
    {
        _onChange?.Dispose();

        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        _onChange?.Dispose();

        return base.DisposeAsync();
    }
}
