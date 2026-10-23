# parity - agent notes

One tool, one purpose: reverse-engineer a legacy WinForms module into a citable manifest and gate
the spec written from it. Read `README.md` first, then `docs/design.md`. Everything project-specific
(the adopter's, whoever they are) lives in the adopter's config, never here.

- Build: `dotnet build`. Test: `dotnet test`. Run: `bin/parity <cmd>`. Fixture run: see `examples/PolicyWizard/README.md`.
- The manifest schema (`src/Parity/Model/Manifest.cs`) is the contract; bump `Extractor.SchemaVersion` on a breaking change.
- A designer statement the extractor cannot interpret must land in `raw`, never be dropped. Tests assert raw == 0 on the fixture.
- Plain "-" in prose, never an em dash. Quality over development cost. Reproduce before fixing.

## Memory (vault)

Durable decisions and history for this project live in Denys's vault at `~/memory/projects/parity/`
(`plan.md` facts, `log.md` dated events). Do not keep memory in this repo or in provider-local stores.
