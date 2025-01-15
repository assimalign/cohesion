using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

internal class ConfigurationJsonProvider : ConfigurationProvider
{
    private readonly Stream stream;

    ConfigurationJsonProvider(Stream stream)
    {
        this.stream = stream;
    }

    public override string Name => throw new NotImplementedException();

    public override async Task LoadAsync(cancellationToken)
    {
        
    }

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var element = document.RootElement;

        element.W

    }

    public override void Dispose()
    {
        stream.Position = 0;

        using var writer = new Utf8JsonWriter(stream);



        stream.Dispose();
    }




    private void Write(Utf8JsonWriter writer)
    {
        foreach (var entry in Data)
        {
            if (entry is IConfigurationSection section)
            {
                Write(writer, section);
            }
            else if (entry is IConfigurationValue value)
            {
                Write(writer, value);
            }
            else
            {
                throw new Exception("Unexpected entry");
            }
        }
    }

    private void Write(Utf8JsonWriter writer, IConfigurationValue value)
    {
        
    }

    private void Write(Utf8JsonWriter writer, IConfigurationSection section)
    {

    }

    




    #region Helpers
    public static IConfigurationProvider FromFile(FileSystemPath path)
    {
        return default;
    }

    public static IConfigurationProvider FromStream(Stream stream)
    {

    }

    

    #endregion
}
