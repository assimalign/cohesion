# Assimalign.Cohesion.Dns &#8212; Provenance audit

This document captures the audit and legal-remediation work performed at
the opening of the L01.01.08 DNS epic. It exists so future contributors
can see what was removed, why, and what rules apply going forward.

## Finding

A scan of `libraries/Dns/` at the start of the epic found **118 of 126
source files** carrying this header:

```text
/*
Technitium Library
Copyright (C) 2024  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
...
*/
```

The Cohesion repository ships under the **MIT License** (see `LICENSE`
at the root of the repository). GPL v3+ is a copyleft license that is
not compatible with MIT redistribution: GPL code embedded in an MIT
project would cause the project's MIT grant to be a license violation
against the GPL copyright holder, because MIT does not preserve the
copyleft propagation required by GPL.

The 8 files without a GPL header were short stubs (enums, scaffolding,
two empty placeholder tests) in the same code style and referencing the
same private types as the headered files. They were treated as part of
the same codebase for the purpose of this audit; missing a header is
not evidence of independent authorship.

## Action taken

The opening PR of the L01.01.08 epic removed **every C# source file**
under `libraries/Dns/`. The csproj files were rewritten to match the
current Cohesion conventions. No GPL-headered file is referenced or
retained anywhere in the repository's working tree at the end of that
PR.

The git history before the removal still contains the GPL-headered
versions of those files. Cleaning history (rebase + force-push to
`main`) would be required to remove the historical record entirely;
that's a project-level decision tracked separately from this epic.

## Rules going forward

The DNS area is being rebuilt as a **clean-room implementation from
published RFCs**. Concretely:

- **Read the RFC, not the prior code.** Wire layouts, field widths, RR
  type numbers, RCODE values, and other protocol facts come from
  documents like RFC 1035 (basic DNS), RFC 6891 (EDNS), RFC 4033-4035
  (DNSSEC), RFC 7858 (DoT), RFC 8484 (DoH), RFC 9250 (DoQ), RFC 9460
  (SVCB/HTTPS), RFC 6698 (DANE/TLSA). Facts in those documents are not
  copyrightable.
- **Do not paste or transcribe.** Code expression is copyrightable even
  when the facts it encodes are not. Do not look at the Technitium
  source, or any other DNS library you have read with a restrictive
  license, while writing the Cohesion implementation.
- **Document non-trivial references.** When the implementation departs
  from or extends an RFC (e.g., implementation-defined caching policy),
  cite the RFC by number and section in code comments.
- **Apply this rule across the family.** The same posture applies to
  any future package under `Assimalign.Cohesion.Dns.*` — clean-room
  rules don't stop at the package boundary.

## Inventory at removal time

For the record, the deleted files broke down roughly as:

| Category | Count | Notes |
|----------|------:|-------|
| DNS protocol core (packets, names, RRs, EDNS, DNSSEC) | ~60 | Wire-format work, replaces with clean-room implementation. |
| HTTP machinery (`HttpRequest`, `HttpResponse`, `HttpChunkedStream`, &#8230;) | 7 | Off-topic for a DNS library; not retained in any form. |
| Proxy machinery (`SocksProxy`, `HttpProxy`, `TransparentProxyServer`, &#8230;) | 29 | Off-topic; not retained. |
| Network primitives (`NetworkAddress`, `NetworkMap`, `NetworkAccessControl`) | 9 | Will rebuild only what the DNS contracts genuinely need. |
| Other (`ProxyProtocolStream`, extension classes) | ~5 | Off-topic; not retained. |
| Short stubs / empty placeholders | 8 | Replaced. |

## Status of this document

Frozen after the opening PR lands. Updates only if new provenance
issues are discovered later in the epic.
