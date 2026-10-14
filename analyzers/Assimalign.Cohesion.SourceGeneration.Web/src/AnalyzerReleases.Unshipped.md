; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
COHWEB0001 | Web | Error | Typed endpoint handler is a delegate instance, not a lambda or method group
COHWEB0002 | Web | Error | Typed endpoint handler returns a type the endpoint cannot write
COHWEB0003 | Web | Error | Typed endpoint handler parameter cannot be bound
COHWEB0004 | Web | Error | Typed endpoint handler binds more than one request body
COHWEB0005 | Web | Error | Typed endpoint handler binds both a request body and form fields
COHWEB0006 | Web | Error | Typed endpoint handler delegate type cannot be named by generated code
COHWEB0007 | Web | Error | Typed endpoint reads or writes serialized content without Web.Serialization
