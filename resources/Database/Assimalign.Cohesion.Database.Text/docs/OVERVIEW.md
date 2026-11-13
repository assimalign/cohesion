# Database.Text

`Assimalign.Cohesion.Database.Text` is the independent text-analysis child root of
Database (#1626). It depends only on `Database.Types`. The Database root
aggregates it; Text never references that root, an engine, storage, or a language.

`TextAnalyzer.Simple` is the default: split Unicode letter/decimal-digit runs with marks continuing open words and
apply Types' pinned simple case fold. `TextAnalyzer.English` additionally removes
the 127 Snowball English stop words and applies an in-house C# Porter2 stemmer.
Both preserve accents by default. Opt into accent removal with an immutable,
persistable descriptor:

```csharp
var descriptor = TextAnalyzerDescriptor.Create("english", 1, foldAccents: true);
var analyzer = TextAnalyzer.Create(descriptor);
TextVector value = analyzer.Analyze("The café runners were running.");
```

Persist the descriptor's exact `Name`, `Version`, and `FoldAccents` beside any data
derived from it. Resolving an unavailable name or version raises `TextException`
with a model-independent code. The model must refuse or rebuild its index;
Text does not silently upgrade the descriptor.

`TextVector`, `TextLexeme`, and `TextPosition` provide immutable, structurally
comparable tsvector values with weighted positions and bounded payloads. Explicit
construction performs no analysis. Positions retain stop-word gaps and clamp at
16,383. `TextWeight.D` is the default. The [design](DESIGN.md) records PostgreSQL
17 oracle results, tokenizer departures, limits, version fingerprints, allocation
bounds, licensing and AOT behavior.

Query syntax, ranking, rendering, posting storage and model-level SQL, OQL or GQL
behavior are non-goals.

The ordinary test project runs without PostgreSQL or a local packed SDK. Its
checked-in oracle data was captured from PostgreSQL 17.11. The NativeAOT guard is
`samples/Assimalign.Cohesion.Database.Text.AotSample`; it checks analysis, folding, descriptor refusal and vector values.
