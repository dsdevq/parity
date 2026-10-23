# Example: PolicyWizard

What a module folder looks like after `extract`, `capture` and `specify`, for the synthetic legacy
project under `fixtures/SampleLegacy`. `manifests/` is not committed here because it is generated:

```sh
bin/parity extract --config fixtures/extract.config.json --module PolicyWizard --forms 'PolicyWizardStep3'
# writes .out/legacy/PolicyWizard/manifests/PolicyWizardStep3.{json,md}
```

`spec/PolicyWizardStep3.md` is a complete, gate-passing spec for that manifest (the tests patch the
manifest hash in before linting). `fixtures/*.json` are recorded service calls in the shape the lint expects.
