using System;

namespace Assimalign.Cohesion.Configuration.Xml;

using Assimalign.Cohesion.Configuration.FileSystem;

/// <summary>
/// Represents options used to configure XML file-backed configuration providers.
/// </summary>
public class ConfigurationXmlOptions : FileSystemConfigurationOptions
{
    private XmlDocumentDecryptor _decryptor = XmlDocumentDecryptor.Instance;

    /// <summary>
    /// Gets or sets the decryptor used to read encrypted XML configuration documents.
    /// </summary>
    public XmlDocumentDecryptor Decryptor
    {
        get => _decryptor;
        set => _decryptor = value ?? throw new ArgumentNullException(nameof(value));
    }
}
