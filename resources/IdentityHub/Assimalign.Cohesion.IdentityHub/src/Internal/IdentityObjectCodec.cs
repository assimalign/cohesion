using System;
using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The codec of one kind of directory object, with what <see cref="IdentityObjectSet{TObject}"/>
/// needs to serve the kind: where an object's identifier and name are, how it is marked locked
/// or stored, and what a refusal says.
/// </summary>
/// <remarks>
/// Every kind is stored the same way. The record's key is the text of the object's identifier,
/// its name, in lowercase, is one unique <c>name</c> index entry, and the rest is a JSON payload.
/// The unique index is what makes "no two objects hold one name" hold when two writers race: the
/// store refuses the second write.
/// </remarks>
/// <typeparam name="TObject">The directory object.</typeparam>
internal abstract class IdentityObjectCodec<TObject> : IIdentityHubRecordCodec<TObject>
    where TObject : class
{
    // The payload is never placed in markup, so only what JSON itself requires is escaped.
    private static readonly JsonWriterOptions _writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public abstract IdentityHubRecordSetName Set { get; }

    /// <summary>
    /// Gets the message for a <see langword="null"/> entry among the objects defined in code.
    /// </summary>
    internal abstract string RequiredMessage { get; }

    /// <summary>
    /// Gets the message for one identifier defined twice. <c>{0}</c> is the object.
    /// </summary>
    internal abstract string DuplicateMessage { get; }

    /// <summary>
    /// Gets the message for one name defined twice. <c>{0}</c> is the name, and <c>{1}</c> and
    /// <c>{2}</c> are the objects.
    /// </summary>
    internal abstract string DuplicateNameMessage { get; }

    public abstract IdentityHubRecord Encode(TObject value);

    public abstract TObject Decode(IdentityHubRecord record);

    /// <summary>
    /// Returns the object's identifier.
    /// </summary>
    internal abstract Guid IdOf(TObject value);

    /// <summary>
    /// Returns the object's name as it is compared and indexed: in lowercase.
    /// </summary>
    internal abstract string NameOf(TObject value);

    /// <summary>
    /// Returns what a message calls the object.
    /// </summary>
    internal abstract string Describe(TObject value);

    /// <summary>
    /// Returns the object as a registry serves one defined in code.
    /// </summary>
    internal abstract TObject AsLocked(TObject value);

    /// <summary>
    /// Returns the object as a registry serves one read from its store.
    /// </summary>
    internal abstract TObject AsStored(TObject value, IdentityHubRecordVersion version);

    /// <summary>
    /// Returns the key an object with an identifier is stored under.
    /// </summary>
    internal static IdentityHubRecordKey KeyOf(Guid id)
    {
        return new IdentityHubRecordKey(IdentityDirectoryGrammar.WriteId(id));
    }

    /// <summary>
    /// Returns the unique index entry that keeps a name to one object.
    /// </summary>
    internal static IdentityHubRecordIndex NameEntry(string name)
    {
        return new IdentityHubRecordIndex(IdentityObjectIndexes.Name, new IdentityHubRecordKey(name), isUnique: true);
    }

    /// <summary>
    /// Builds an object's record: its identifier as the key, its lowercase name as the unique
    /// index entry, and its payload as JSON.
    /// </summary>
    protected static IdentityHubRecord Write<TPayload>(
        Guid id,
        string name,
        TPayload payload,
        JsonTypeInfo<TPayload> layout)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, _writerOptions))
        {
            JsonSerializer.Serialize(writer, payload, layout);
        }

        return new IdentityHubRecord(KeyOf(id), buffer.WrittenMemory, expiresAt: null, [NameEntry(name)]);
    }

    /// <summary>
    /// Reads a record's payload, or returns <see langword="null"/> when the payload is the JSON
    /// <c>null</c>.
    /// </summary>
    protected static TPayload? Read<TPayload>(IdentityHubRecord record, JsonTypeInfo<TPayload> layout)
        where TPayload : class
    {
        return JsonSerializer.Deserialize(record.Payload.Span, layout);
    }
}
