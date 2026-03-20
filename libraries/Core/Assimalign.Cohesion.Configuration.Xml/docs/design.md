# Assimalign.Cohesion.Configuration.Xml Design

## Design Intent

The package translates XML documents into the shared key and value configuration model while keeping XML-specific concerns, such as reader settings and decryptor support, inside the provider layer.

## Architecture

- Builder extensions provide the registration surface for files and streams.
- The XML stream provider turns document structure into flattened configuration keys.
- An XmlDocumentDecryptor hook keeps encrypted XML scenarios inside the format package instead of leaking them into callers.

## Layout Example

```text
Assimalign.Cohesion.Configuration.Xml/
  src/
    Assimalign.Cohesion.Configuration.Xml.csproj
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register a file-based provider

```csharp
var configuration = new ConfigurationBuilder()
    .AddXmlFile("appsettings.xml")
    .Build();
```

## Example 2: Register a stream-based provider

```csharp
using var stream = File.OpenRead("appsettings.xml");

var configuration = new ConfigurationBuilder()
    .AddXmlStream(stream)
    .Build();
```
