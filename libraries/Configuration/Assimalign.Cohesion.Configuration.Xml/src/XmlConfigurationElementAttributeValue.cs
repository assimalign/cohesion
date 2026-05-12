using System;

namespace Assimalign.Cohesion.Configuration.Xml;

internal sealed class XmlConfigurationElementAttributeValue
{
    public XmlConfigurationElementAttributeValue(
        string attribute,
        string value,
        int? lineNumber,
        int? linePosition)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(value);

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
