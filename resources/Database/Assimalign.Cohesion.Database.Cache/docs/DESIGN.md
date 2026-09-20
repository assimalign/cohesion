# Assimalign.Cohesion.Database.Cache — Design

Cache is deferred behind the Key-Value engine and currently has no implemented
engine, persistence, or lifecycle. Its empty source and test stubs do not define
the future Cache model architecture.

The only build correction in Phase 2 is the test project's item classification:
`xunit.runner.json` is content rather than a C# compile item. The stub test remains
unchanged. Future runtime implementation must compose the shared Database kernel
and follow the resource-area reference rules.

The test project references runner packages; runner configuration is a content
file owned by that project. This diagram shows those existing build dependencies.

```mermaid
flowchart LR
    Tests["Cache test project"] --> Runner["xUnit and test runner packages"]
    Tests --> Config["xunit.runner.json content"]
```
