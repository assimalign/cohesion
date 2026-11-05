# Format-probe crash fixture

Test-only executable for #1744. Arguments are model, root directory and current/legacy/index.
It exits with live committed and uncommitted journal state. The owning engine tests run the
real open path and compare original files or verify committed bytes after torn-page recovery.
See DESIGN.md for the crash image and dependency rules.

The temp-lease-probe/root mode verifies SQL startup availability and coded spill refusal with
disabled Unix locking, using a small journal threshold and the real file engine.
