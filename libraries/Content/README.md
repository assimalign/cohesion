# Content

The Content area is the low-level content and format library family (L1): retained parsers,
serializers, document models, and the format-neutral contracts they share. It exists to give later
services — a static content engine, MediaHub — and sibling foundations (for example OpenApi's YAML
serialization) standards-driven format machinery without service or storage coupling.

Tracked by area epic [L01.01.05] Foundation - Content (#14).

## Layering

- **L1 (this area):** content contracts and format packages — pure description and parsing machinery,
  no hosting or service runtime concerns.
- **L2/L3 (future):** services compose these packages through the root contracts; media workflow and
  job orchestration explicitly live at the service layer, not here.

## Project family

Dependency direction is one-way and root-anchored: format packages depend on the shared intermediate
layers, never on each other's internals.

| Package | Responsibility | Depends on | Status |
|---|---|---|---|
| [`Assimalign.Cohesion.Content`](./Assimalign.Cohesion.Content/) | Root contracts: identity, format metadata, stream ownership, composition, reader/writer seam, exceptions | — | Implemented |
| `Assimalign.Cohesion.Content.Binary` | Shared binary parsing surface (exact reads, bounded slices) | Content | Placeholder |
| [`Assimalign.Cohesion.Content.Text`](./Assimalign.Cohesion.Content.Text/) | Encoding detection and text reading surface | Content | Implemented |
| `Assimalign.Cohesion.Content.Yaml` | YAML 1.2 document model, parser, emitter | Content, Content.Text | In design |
| `Assimalign.Cohesion.Content.Markdown` | Markdown document model and parser | Content.Text | Placeholder |
| `Assimalign.Cohesion.Content.Media` | Shared media abstractions | Content.Binary | Placeholder |
| `Assimalign.Cohesion.Content.Bmff` | ISO BMFF box model and reader/writer | Content.Media | Broken — repair tracked (#442) |
| `Assimalign.Cohesion.Content.Mpeg` | MP4/MPEG-4 profile over BMFF | Content.Bmff | Placeholder |
| `Assimalign.Cohesion.Content.Ebml` | EBML (RFC 8794) element model | Content | Broken — repair tracked (#458) |
| `Assimalign.Cohesion.Content.Mkv` | Matroska over EBML | Content.Ebml | Placeholder |
| `Assimalign.Cohesion.Content.Pdf` | PDF document structure and parser | Content | Placeholder |
| `Assimalign.Cohesion.Content.Exe` | Executable inspection and invocation | Content.Binary | Placeholder |

## Standards

Each retained format names its governing specification (BMFF: ISO/IEC 14496-12; EBML: RFC 8794;
YAML: yaml.org/spec/1.2.2; Markdown: CommonMark baseline) and is validated with unit plus corpus or
compliance coverage where a formal specification exists.

See each package's `docs/OVERVIEW.md` and `docs/DESIGN.md` for detail.
