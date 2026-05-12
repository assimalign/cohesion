using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Assimalign.Cohesion.Configuration.Xml;

using Assimalign.Cohesion.Configuration;

internal static class XmlConfigurationParser
{
    private const string NameAttributeKey = "Name";

    public static void Parse(
        Stream stream,
        IDictionary<Path, string?> entries,
        XmlDocumentDecryptor decryptor)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(decryptor);

        var settings = new XmlReaderSettings
        {
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        XmlConfigurationElement? root = null;

        using XmlReader reader = decryptor.CreateDecryptingXmlReader(stream, settings);

        var currentPath = new Stack<XmlConfigurationElement>();
        XmlNodeType previousNodeType = reader.NodeType;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var element = new XmlConfigurationElement(reader.LocalName, GetName(reader));

                    if (currentPath.Count == 0)
                    {
                        root = element;
                    }
                    else
                    {
                        AddChild(currentPath.Peek(), element);
                    }

                    currentPath.Push(element);
                    ReadAttributes(reader, element);

                    if (reader.IsEmptyElement)
                    {
                        currentPath.Pop();
                    }

                    break;

                case XmlNodeType.EndElement:
                    if (currentPath.Count != 0)
                    {
                        XmlConfigurationElement parent = currentPath.Pop();

                        if (previousNodeType == XmlNodeType.Element)
                        {
                            var lineInfo = reader as IXmlLineInfo;
                            parent.TextContent = new XmlConfigurationElementTextContent(
                                string.Empty,
                                lineInfo?.LineNumber,
                                lineInfo?.LinePosition);
                        }
                    }

                    break;

                case XmlNodeType.CDATA:
                case XmlNodeType.Text:
                    if (currentPath.Count != 0)
                    {
                        var lineInfo = reader as IXmlLineInfo;
                        currentPath.Peek().TextContent = new XmlConfigurationElementTextContent(
                            reader.Value,
                            lineInfo?.LineNumber,
                            lineInfo?.LinePosition);
                    }

                    break;

                case XmlNodeType.XmlDeclaration:
                case XmlNodeType.ProcessingInstruction:
                case XmlNodeType.Comment:
                case XmlNodeType.Whitespace:
                    break;

                default:
                    throw new FormatException(
                        $"The XML node type '{reader.NodeType}' is not supported{FormatLineInfo(reader as IXmlLineInfo)}.");
            }

            previousNodeType = reader.NodeType;

            if (previousNodeType == XmlNodeType.Element && reader.IsEmptyElement)
            {
                previousNodeType = XmlNodeType.EndElement;
            }
        }

        ProvideConfiguration(root, entries);
    }

    private static void AddChild(XmlConfigurationElement parent, XmlConfigurationElement child)
    {
        if (parent.ChildrenBySiblingName is not null)
        {
            if (!parent.ChildrenBySiblingName.TryGetValue(child.SiblingName, out List<XmlConfigurationElement>? siblings))
            {
                siblings = [];
                parent.ChildrenBySiblingName.Add(child.SiblingName, siblings);
            }

            siblings.Add(child);
            return;
        }

        if (parent.SingleChild is null)
        {
            parent.SingleChild = child;
            return;
        }

        var children = new Dictionary<string, List<XmlConfigurationElement>>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(parent.SingleChild.SiblingName, child.SiblingName, StringComparison.OrdinalIgnoreCase))
        {
            children.Add(child.SiblingName, [parent.SingleChild, child]);
        }
        else
        {
            children.Add(parent.SingleChild.SiblingName, [parent.SingleChild]);
            children.Add(child.SiblingName, [child]);
        }

        parent.ChildrenBySiblingName = children;
        parent.SingleChild = null;
    }

    private static void ReadAttributes(XmlReader reader, XmlConfigurationElement element)
    {
        if (reader.AttributeCount <= 0)
        {
            return;
        }

        element.Attributes = [];
        var lineInfo = reader as IXmlLineInfo;

        for (int index = 0; index < reader.AttributeCount; index++)
        {
            reader.MoveToAttribute(index);

            if (!string.IsNullOrEmpty(reader.NamespaceURI))
            {
                throw new FormatException(
                    $"XML namespaces are not supported on configuration attributes{FormatLineInfo(lineInfo)}.");
            }

            element.Attributes.Add(new XmlConfigurationElementAttributeValue(
                reader.LocalName,
                reader.Value,
                lineInfo?.LineNumber,
                lineInfo?.LinePosition));
        }

        reader.MoveToElement();
    }

    private static string? GetName(XmlReader reader)
    {
        string? name = null;

        while (reader.MoveToNextAttribute())
        {
            if (!string.Equals(reader.LocalName, NameAttributeKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(reader.NamespaceURI))
            {
                throw new FormatException("XML namespaces are not supported on the Name attribute.");
            }

            name = reader.Value;
            break;
        }

        reader.MoveToElement();

        return name;
    }

    private static void ProvideConfiguration(XmlConfigurationElement? root, IDictionary<Path, string?> entries)
    {
        if (root is null)
        {
            return;
        }

        var prefix = new List<Key>();

        if (!string.IsNullOrEmpty(root.Name))
        {
            prefix.Add(new Key(root.Name));
        }

        ProcessElement(prefix, root, entries);
    }

    private static void ProcessElement(
        List<Key> prefix,
        XmlConfigurationElement element,
        IDictionary<Path, string?> entries)
    {
        if (element.TextContent is not null
            && (element.Attributes is not null
                || element.SingleChild is not null
                || element.ChildrenBySiblingName is not null))
        {
            throw new FormatException(
                $"The XML element '{element.ElementName}' cannot contain both direct text content and attributes or child elements when using the composite configuration model.");
        }

        ProcessAttributes(prefix, element, entries);
        ProcessContent(prefix, element, entries);
        ProcessChildren(prefix, element, entries);
    }

    private static void ProcessAttributes(
        List<Key> prefix,
        XmlConfigurationElement element,
        IDictionary<Path, string?> entries)
    {
        if (element.Attributes is null)
        {
            return;
        }

        for (int index = 0; index < element.Attributes.Count; index++)
        {
            XmlConfigurationElementAttributeValue attribute = element.Attributes[index];

            prefix.Add(new Key(attribute.Attribute));
            AddEntry(entries, prefix, attribute.Value, attribute.LineNumber, attribute.LinePosition);
            prefix.RemoveAt(prefix.Count - 1);
        }
    }

    private static void ProcessContent(
        List<Key> prefix,
        XmlConfigurationElement element,
        IDictionary<Path, string?> entries)
    {
        if (element.TextContent is null)
        {
            return;
        }

        AddEntry(
            entries,
            prefix,
            element.TextContent.TextContent,
            element.TextContent.LineNumber,
            element.TextContent.LinePosition);
    }

    private static void ProcessChildren(
        List<Key> prefix,
        XmlConfigurationElement element,
        IDictionary<Path, string?> entries)
    {
        if (element.SingleChild is not null)
        {
            ProcessChild(prefix, element.SingleChild, null, entries);
            return;
        }

        if (element.ChildrenBySiblingName is null)
        {
            return;
        }

        foreach (List<XmlConfigurationElement> siblings in element.ChildrenBySiblingName.Values)
        {
            if (siblings.Count == 1)
            {
                ProcessChild(prefix, siblings[0], null, entries);
                continue;
            }

            for (int index = 0; index < siblings.Count; index++)
            {
                ProcessChild(prefix, siblings[index], index, entries);
            }
        }
    }

    private static void ProcessChild(
        List<Key> prefix,
        XmlConfigurationElement child,
        int? index,
        IDictionary<Path, string?> entries)
    {
        prefix.Add(new Key(child.ElementName));

        bool hasName = !string.IsNullOrEmpty(child.Name);
        if (hasName)
        {
            prefix.Add(new Key(child.Name!));
        }

        if (index is not null)
        {
            prefix.Add(new Key(index.Value.ToString(CultureInfo.InvariantCulture)));
        }

        ProcessElement(prefix, child, entries);

        if (index is not null)
        {
            prefix.RemoveAt(prefix.Count - 1);
        }

        if (hasName)
        {
            prefix.RemoveAt(prefix.Count - 1);
        }

        prefix.RemoveAt(prefix.Count - 1);
    }

    private static void AddEntry(
        IDictionary<Path, string?> entries,
        List<Key> prefix,
        string value,
        int? lineNumber,
        int? linePosition)
    {
        if (prefix.Count == 0)
        {
            throw new FormatException(
                $"Root XML content cannot be mapped without a Name attribute{FormatLineInfo(lineNumber, linePosition)}.");
        }

        Path path = CreatePath(prefix);

        if (entries.ContainsKey(path))
        {
            throw new FormatException(
                $"A duplicate XML configuration key '{path}' was found{FormatLineInfo(lineNumber, linePosition)}.");
        }

        entries.Add(path, value);
    }

    private static Path CreatePath(List<Key> prefix)
    {
        var keys = new Key[prefix.Count];
        prefix.CopyTo(keys, 0);

        return new Path(keys);
    }

    private static string FormatLineInfo(IXmlLineInfo? lineInfo)
    {
        return lineInfo is null ? string.Empty : FormatLineInfo(lineInfo.LineNumber, lineInfo.LinePosition);
    }

    private static string FormatLineInfo(int? lineNumber, int? linePosition)
    {
        return lineNumber is null || linePosition is null
            ? string.Empty
            : $" at line {lineNumber.Value}, position {linePosition.Value}";
    }
}
