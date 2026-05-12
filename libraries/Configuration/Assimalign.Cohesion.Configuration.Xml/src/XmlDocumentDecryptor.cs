using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Assimalign.Cohesion.Configuration.Xml;

/// <summary>
/// Provides XML document decryption support for encrypted XML configuration files.
/// </summary>
public class XmlDocumentDecryptor
{
    private readonly Func<XmlDocument, EncryptedXml>? _encryptedXmlFactory;

    /// <summary>
    /// Gets the shared XML document decryptor instance.
    /// </summary>
    public static XmlDocumentDecryptor Instance { get; } = new();

    /// <summary>
    /// Initializes a new XML document decryptor.
    /// </summary>
    protected XmlDocumentDecryptor()
    {
    }

    internal XmlDocumentDecryptor(Func<XmlDocument, EncryptedXml> encryptedXmlFactory)
    {
        ArgumentNullException.ThrowIfNull(encryptedXmlFactory);
        _encryptedXmlFactory = encryptedXmlFactory;
    }

    /// <summary>
    /// Creates an <see cref="XmlReader"/> that transparently decrypts the XML document when necessary.
    /// </summary>
    /// <param name="input">The input stream to read.</param>
    /// <param name="settings">The XML reader settings to apply.</param>
    /// <returns>The decrypting XML reader.</returns>
    public XmlReader CreateDecryptingXmlReader(Stream input, XmlReaderSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buffer = new MemoryStream();
        input.CopyTo(buffer);
        buffer.Position = 0;

        var document = new XmlDocument();
        using (XmlReader reader = XmlReader.Create(buffer, settings))
        {
            document.Load(reader);
        }

        buffer.Position = 0;

        if (!ContainsEncryptedData(document))
        {
            return XmlReader.Create(buffer, settings);
        }

#pragma warning disable CA1416
        return DecryptDocumentAndCreateXmlReader(document);
#pragma warning restore CA1416
    }

    /// <summary>
    /// Decrypts an encrypted XML document and creates an <see cref="XmlReader"/> over the decrypted content.
    /// </summary>
    /// <param name="document">The XML document to decrypt.</param>
    /// <returns>The decrypting XML reader.</returns>
    [UnsupportedOSPlatform("browser")]
    [RequiresUnreferencedCode("Encrypted XML can reference algorithm implementations that trimming may remove.")]
    [RequiresDynamicCode("Encrypted XML processing can rely on transforms that require dynamic code.")]
    protected virtual XmlReader DecryptDocumentAndCreateXmlReader(XmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        EncryptedXml encryptedXml = _encryptedXmlFactory?.Invoke(document) ?? new EncryptedXml(document);
        encryptedXml.DecryptDocument();

        return document.CreateNavigator()!.ReadSubtree();
    }

    private static bool ContainsEncryptedData(XmlDocument document)
    {
        var namespaceManager = new XmlNamespaceManager(document.NameTable);
        namespaceManager.AddNamespace("enc", "http://www.w3.org/2001/04/xmlenc#");

        return document.SelectSingleNode("//enc:EncryptedData", namespaceManager) is not null;
    }
}
