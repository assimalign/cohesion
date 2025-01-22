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

internal class JsonConfigurationProvider
    : ConfigurationProvider
{
    private readonly Stream source;

    public JsonConfigurationProvider(Stream stream) : base()
    {
        this.source = stream;
    }

    public JsonConfigurationProvider(Stream stream, KeyComparer comparer) : base(comparer)
    {
        this.source = stream;
    }

    public override string Name => nameof(JsonConfigurationProvider);

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        source.Position = 0;

        var reader = new Utf8JsonReader()
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var element = document.RootElement;

    }

    public override void Dispose()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        Write(writer);

        source.SetLength(stream.Length);
        source.Position = 0;

        stream.Position = 0;
        stream.CopyTo(source);

        stream.Dispose();
        source.Dispose();
    }


    private void Read(ref Utf8JsonReader reader)
    {
        reader.Read
    }




    private void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        foreach (var entry in GetEntries())
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

        writer.WriteEndObject();
    }

    private void Write(Utf8JsonWriter writer, IConfigurationValue value)
    {
        writer.WritePropertyName(value.Key);

    }

    private void Write(Utf8JsonWriter writer, IConfigurationSection section)
    {

    }





    #region Helpers

    public static JsonConfigurationProvider FromStream(Stream stream)
    {

    }

    public static JsonConfigurationProvider FromJson(ReadOnlySpan<byte> json)
    {
        var stream = new MemoryStream();

        stream.Write(json);
        stream.Position = 0;

        return FromStream(stream);
    }

    public static JsonConfigurationProvider FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            throw new Exception();
        }

        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(json);

        return FromJson(bytes);
    }



    #endregion
}
