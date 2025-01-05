using System;

namespace Assimalign.Extensions.Configuration.Providers;


internal sealed class XmlConfigurationElementTextContent
{
    public XmlConfigurationElementTextContent(string textContent, int? linePosition, int? lineNumber)
    {
        ThrowHelper.ThrowIfNull(textContent);

        TextContent = textContent;
        LineNumber = lineNumber;
        LinePosition = linePosition;
    }

    public string TextContent { get; }

    public int? LineNumber { get; }

    public int? LinePosition { get; }
}
