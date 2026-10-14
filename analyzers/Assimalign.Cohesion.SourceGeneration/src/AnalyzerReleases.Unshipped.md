; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
COHCMP0001 | ComponentModel | Warning | Component integration declaration is malformed or unresolvable
COHCMP0002 | ComponentModel | Warning | Factory or signature is not externally visible
COHCMP0003 | ComponentModel | Warning | Factory method has an unsupported shape
COHCMP0004 | ComponentModel | Warning | Projection duplicates an earlier integration
COHCMP0005 | ComponentModel | Info | Contributor is present but its target seam is absent
COHCMP0006 | ComponentModel | Warning | Consumer language version cannot declare extension members
COHCMP0007 | ComponentModel | Warning | Disposable product is returned as an instance
