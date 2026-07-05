; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
OPENAPIATTR0001 | OpenApi | Error | [OpenApiOperation] declares an empty path
OPENAPIATTR0002 | OpenApi | Error | Body declares both a model type and a schema reference
OPENAPIATTR0003 | OpenApi | Warning | Path parameter generated as required
OPENAPIATTR0004 | OpenApi | Error | Example declares conflicting value fields
OPENAPIATTR0006 | OpenApi | Error | API key scheme missing parameter name or location
