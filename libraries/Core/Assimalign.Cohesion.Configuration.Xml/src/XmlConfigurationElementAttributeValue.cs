using System;

namespace Assimalign.Extensions.Configuration.Providers;


internal sealed class XmlConfigurationElementAttributeValue
{
    public XmlConfigurationElementAttributeValue(string attribute, string value, int? lineNumber, int? linePosition)
    {
        ThrowHelper.ThrowIfNull(attribute);
        ThrowHelper.ThrowIfNull(value);

        Attribute = attribute;
        Value = value;
        LineNumber = lineNumber;
        LinePosition = linePosition;
    }

    public string Attribute { get; }

    public string Value { get; }

    public int? LineNumber { get; }

    public int? LinePosition { get; }
}
