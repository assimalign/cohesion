using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Xml;

internal sealed class XmlConfigurationElement
{
    public XmlConfigurationElement(string elementName, string? name)
    {
        ArgumentNullException.ThrowIfNull(elementName);

        ElementName = elementName;
        Name = name;
        SiblingName = string.IsNullOrEmpty(Name) ? ElementName : $"{ElementName}:{Name}";
    }

    public string ElementName { get; }

    public string? Name { get; }

    public string SiblingName { get; }

    public Dictionary<string, List<XmlConfigurationElement>>? ChildrenBySiblingName { get; set; }

    public XmlConfigurationElement? SingleChild { get; set; }

    public XmlConfigurationElementTextContent? TextContent { get; set; }

    public List<XmlConfigurationElementAttributeValue>? Attributes { get; set; }
}
