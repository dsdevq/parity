# Example: PolicyWizard

What a module folder looks like after `extract`, `capture` and `specify`, for the synthetic legacy
project under `fixtures/SampleLegacy`. `manifests/` is not committed here because it is generated:

```sh
bin/parity extract --config fixtures/extract.config.json --module PolicyWizard --forms 'PolicyWizardStep3'
# writes .out/legacy/PolicyWizard/manifests/PolicyWizardStep3.{json,md}
```

`spec/PolicyWizardStep3.md` is a complete, gate-passing spec for that manifest (the tests patch the
manifest hash in before linting). `fixtures/*.json` are recorded service calls in the shape the lint expects.

`registry/` is the map phase's output for the whole module (`docs/adopt.md`, The map phase and the registries):
two scenarios around the beneficiary host and the cover summary, one decision, and an error case of
`svc:IPolicyService.AddBeneficiary` backed by `fixtures/add-beneficiary.duplicate.json`. Its `"module"`
stamp is a placeholder (`sha256:0000000000000000`); the tests write the revision of their extract in its
place, as `parity registry stamp` would after a review. To try it:

```sh
bin/parity extract --config fixtures/extract.config.json --module PolicyWizard --forms '*'
cp -r examples/PolicyWizard/registry examples/PolicyWizard/fixtures .out/legacy/PolicyWizard/
bin/parity registry check .out/legacy/PolicyWizard      # REGISTRY-STALE (the placeholder stamp), and the unreferenced scenario
bin/parity registry stamp .out/legacy/PolicyWizard
bin/parity registry query .out/legacy/PolicyWizard --negative
bin/parity registry catalog .out/legacy/PolicyWizard
```
