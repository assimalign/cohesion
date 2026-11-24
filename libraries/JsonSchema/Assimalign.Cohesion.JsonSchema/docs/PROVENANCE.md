# Assimalign.Cohesion.JsonSchema — Provenance

Third-party material in this project, where it came from, and how to bring it up to date. Nothing
listed here is fetched at build or run time.

## Official meta-schemas

`src/MetaSchemas/` holds the meta-schemas of the five built-in dialects, embedded in the assembly
so that every registry serves them without a resolver.

| Folder | Dialect | Files |
| --- | --- | --- |
| `draft-04/` | `http://json-schema.org/draft-04/schema#` | `schema.json` |
| `draft-06/` | `http://json-schema.org/draft-06/schema#` | `schema.json` |
| `draft-07/` | `http://json-schema.org/draft-07/schema#` | `schema.json` |
| `draft-2019-09/` | `https://json-schema.org/draft/2019-09/schema` | `schema.json` and six vocabulary meta-schemas in `meta/` |
| `draft-2020-12/` | `https://json-schema.org/draft/2020-12/schema` | `schema.json` and eight vocabulary meta-schemas in `meta/` |

- **Source:** the release branches of
  [json-schema-org/json-schema-spec](https://github.com/json-schema-org/json-schema-spec)
  (`draft-04`, `draft-06`, `draft-07`, `2019-09` and `2020-12`), checked byte for byte against the
  documents `json-schema.org` serves at each identifier.
- **Changes:** none. The files are copied as published.
- **License:** the JSON Schema specification repository is dual-licensed under the BSD 3-Clause
  License and the Academic Free License 3.0, © 2022 JSON Schema Specification Authors.
  `src/MetaSchemas/LICENSE` carries both texts.
- **Updating:** published meta-schemas do not change. A new dialect release adds a folder, its
  `EmbeddedResource` items in the csproj (each with a `LogicalName` matching the folder layout), and
  its identifiers in `Internal/Model/MetaSchemaResolver.cs`.

## JSON Schema Test Suite

`tests/TestObjects/JSON-Schema-Test-Suite/` holds the official
[JSON Schema Test Suite](https://github.com/json-schema-org/JSON-Schema-Test-Suite), which the
tests run in every dialect (`docs/DESIGN.md`, "Conformance").

- **Commit:** `e2a68a15cd490750fd6b95e604e4054d7d978d1f`, "test(email): limit RFC 5321-only
  rejections to 2020-12 and later" (#1271).
- **What is vendored:** `LICENSE`, `README.md`, `validation/README.md`, `validation/tests/`,
  `annotations/README.md` and `annotations/tests/`, unmodified. Left out: the repository's tooling
  and CI files, the JSON schemas that describe the test file format, and `validation/tests/proposals/`,
  which tests proposals no released dialect contains.
- **Layout:** the suite's unified layout. Each test file covers every dialect; a test case's
  `compatibility` property names the dialects it applies to, and its `externalSchemas` property
  carries the remote documents it refers to, so no test server is involved.
- **License:** MIT, © 2012 Julian Berman (`LICENSE` in the folder).
- **Updating:** replace the vendored files with those of a newer commit, leaving out the same
  material, record the commit here, and run the tests. A test that now fails is either a library
  bug to fix or an exclusion to add, with its reason, under "Conformance" in `docs/DESIGN.md`.

## Unicode data for IDNA2008

`src/Internal/Formats/IdnaCharacterData.cs` holds the Unicode properties that `idn-hostname` and
`idn-email` need and the BCL does not expose. The general category comes from the runtime
(`CharUnicodeInfo`, which CoreLib carries without ICU); everything else was generated from the
Unicode Character Database.

| Tables | Property | Unicode version | Read with |
| --- | --- | --- | --- |
| `BidiL`, `BidiR`, `BidiAl`, `BidiAn`, `BidiEn`, `BidiEs`, `BidiOn`, `BidiBn` | Bidi_Class (RFC 5893) | 15.1 | CPython `unicodedata` |
| `Virama` | Canonical_Combining_Class = 9 (RFC 5892 Appendix A.1) | 15.1 | CPython `unicodedata` |
| `CaseFoldUnstable` | Full case folding that differs where the lowercase mapping does not (RFC 5892 `Unstable`) | 15.1 | CPython `unicodedata` |
| `JoiningD`, `JoiningL`, `JoiningR`, `JoiningC`, `JoiningT`, `JoiningU` | Joining_Type (RFC 5892 Appendix A.1) | 15.0 | Perl `Unicode::UCD` |
| `ScriptGreek`, `ScriptHebrew`, `ScriptHiragana`, `ScriptKatakana`, `ScriptHan` | Script (RFC 5892 Appendix A.4 to A.7) | 15.0 | Perl `Unicode::UCD` |
| `DefaultIgnorable` | Default_Ignorable_Code_Point (RFC 5892 §2.5, IgnorableProperties) | 15.0 | Perl `Unicode::UCD` |

The derivation rules, which a regeneration must follow:

1. **Candidates only, where it matters.** The Bidi, Script and case-folding tables hold only code
   points a valid label can contain: the general categories Ll, Lu, Lo, Nd, Lm, Mn and Mc, the
   RFC 5892 §2.6 exceptions that are PVALID or CONTEXTO, U+200C and U+200D (CONTEXTJ), the hyphen,
   and ASCII digits and lowercase letters, less the exceptions that are DISALLOWED. Any other code
   point is DISALLOWED before those tables are read. The joining, virama and default-ignorable
   tables are complete.
2. **Ranges merge across non-candidates.** Each table is a sorted list of inclusive
   `[first, last]` pairs, and a range in a candidates-only table may span code points that are not
   candidates, since no lookup reaches them. This is what keeps the tables small.
3. **Properties the category implies are not stored.** A nonspacing or enclosing mark is NSM in Bidi
   terms and Transparent in joining terms unless a table says otherwise; so the joining table stores
   D, L, R and C, stores T only for code points whose category does not imply it, and stores U for
   marks and format characters that are not Transparent.
4. **Right-to-left defaults.** Unassigned code points in the right-to-left blocks get the default
   R or AL that `DerivedBidiClass.txt` assigns them, so a letter that a newer runtime knows and the
   tables predate still reads as right-to-left.
5. **Cross-check.** The UTS #46 mapping the library derives (NFKC case folding, with the mapping
   table's ignored code points and nontransitional deviations) was compared against
   `IdnaMappingTable.txt` for Unicode 18.0 and agrees for every code point that table does not
   disallow.

**Updating:** regenerate every table from one Unicode version by the rules above, replace the arrays,
and run the `idn-hostname` and `idn-email` tests, the official suite's optional format files among
them. The generator was a one-off script and is not checked in; the rules above are the
specification a new one must meet.
