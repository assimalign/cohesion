# Assimalign.Cohesion.Content.Markdown

Markdown for the Content family: a document model, a parser implementing a documented subset of
CommonMark 0.31.2, an HTML renderer, and a canonical Markdown writer with round-trip fidelity.
Parsing never throws — constructs outside the retained subset degrade predictably to literal text.

- [docs/OVERVIEW.md](./docs/OVERVIEW.md) — purpose, scope, and usage
- [docs/DESIGN.md](./docs/DESIGN.md) — the retained subset, degradation table, parser phases, and round-trip guarantee
