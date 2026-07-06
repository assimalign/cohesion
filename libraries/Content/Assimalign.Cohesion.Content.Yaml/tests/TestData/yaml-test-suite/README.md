# Vendored yaml-test-suite corpus

A tracked subset of the official [YAML test suite](https://github.com/yaml/yaml-test-suite)
(MIT license), vendored from the `data` branch at commit
`6ad3d2c62885d82fc349026c136ef560838fdf3d`. Each case directory carries `in.yaml` (the input),
`in.json` (the expected data when the input is valid), `===` (the case name), and `error` (a marker
when the input must be rejected). `YamlTestSuiteTests` runs every vendored case: valid cases must
parse and match the expected JSON semantics; error cases must raise `YamlException`.

## Coverage and known gaps

At vendoring time the parser passed **223 of the suite's 333 runnable cases (67%)**. The vendored
subset is exactly the passing set, so it acts as a regression floor — cases must never silently drop
out. The excluded cases are explicit known gaps, tracked in three groups:

**Valid YAML the parser rejects (24)** — mostly tab separation, exotic property/indentation
combinations, and unusual key shapes:

`26DV 2SXE 5MUD 5T43 5WE3 9MMW A2M4 BEC7 C2DT CFD4 DFF7 E76Z EHF6 HMQ5 K3WX KK5P P2AD P76L PW8X RZP5 SKE5 W5VH WZ62 XW4D`

**Valid YAML that parses with divergent semantics (26)** — folding subtleties, empty-key forms, and
tag interactions:

`4QFQ 57H4 6FWR 6VJK 74H7 7FWL 7T8X 7TMG 9KAX CT4Q DWX9 F6MC H2RW HS5T HWV9 LE5A M7A3 MJS9 NB6Z QT73 R4YG S4JQ T26H U3XV UV7Q ZH7C`

**Invalid YAML the parser accepts (60)** — the parser is deliberately lenient in this revision;
strict rejection of these malformed shapes is the long tail of conformance:

`236B 2CMS 3HFZ 4EJS 4H7K 4HVU 4JVG 5LLU 5TRB 62EZ 6S55 7MNF 8XDJ 9C9N 9CWY 9HCY 9JBA 9KBC BD7L BF9H BS4K C2SP CML9 CVW2 CXX2 DK4H DMG6 EB22 EW3V G5U8 G7JE G9HC GDY7 GT5M H7J7 H7TQ HU3P JY7Z KS4U LHL4 N4JP N782 P2EQ Q4CL QB6E RHX7 RXY3 S98Z SR86 SU5Z SU74 SY6V TD5N U44R U99R W9L4 YJV2 ZCZ6 ZL4Z ZVH3`

Raising the pass rate is tracked by the YAML feature's corpus story; when a gap is fixed, move its
case into this directory so the floor rises.
