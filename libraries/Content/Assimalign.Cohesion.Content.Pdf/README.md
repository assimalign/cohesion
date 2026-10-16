# Assimalign.Cohesion.Content.Pdf

A byte-preserving PDF object model and bounded structural reader for PDF 1.0–1.7 and 2.0,
governed by ISO 32000-2. It reads classic, stream and hybrid cross-references, incremental
revisions, object streams and inherited page attributes. Stream payloads stay lazy until decoded.

PDF needs random access: supply a readable, seekable stream and keep it open while using the
document or its lazy data. Sources are borrowed by default. The package targets .NET 10 and
inherits NativeAOT compatibility; its sole direct dependency is `Content.Binary`.

```csharp
using var source = File.OpenRead("document.pdf");
IContentReader<PdfDocument> reader = new PdfReader();
using var document = reader.Read(source);
Console.WriteLine($"PDF {document.Version}: {document.PageCount} pages");
```

See [usage and limits](docs/OVERVIEW.md) and the [clause-by-clause design](docs/DESIGN.md).
Encryption, content operators, text extraction, rendering and saving are outside this stage.
An encrypted trailer still loads; accessing string bytes or stream data reports `Unsupported`.
Stage 3 adds full save and incremental update on this model.

## Build and tests

From the repository root:

```powershell
dotnet build libraries/Content/Assimalign.Cohesion.Content.Pdf/src/Assimalign.Cohesion.Content.Pdf.csproj
dotnet test libraries/Content/Assimalign.Cohesion.Content.Pdf/tests/Assimalign.Cohesion.Content.Pdf.Tests.csproj
```

A fresh worktree requires the existing build tasks:
`dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj` if MSBuild reports MSB4062.
Tests generate their own PDFs with calculated offsets and also load four checked-in golden PDFs.
No network, local media files, installed PDF viewer, or packed local feed is required.
