using System;

namespace Assimalign.Cohesion.Configuration.Xml;

internal sealed class XmlConfigurationElementTextContent
{
    public XmlConfigurationElementTextContent(
        string textContent,
        int? lineNumber,
        int? linePosition)
    {
        ArgumentNullException.ThrowIfNull(textContent);

        TextContent = textContent;
        LineNumber = lineNumber;
        LinePosition = linePosition;
    }

    public string TextContent { get; }

    public int? LineNumber { get; }

    public int? LinePosition { get; }
}
