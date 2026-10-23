# Example: Payouts

The acceptance unit from the pilot evaluation (#18, `docs/pilot-metrics.md`), as a module folder after
`extract`, `map` and `specify`. `PayeeLookupPanel` under `fixtures/SampleLegacy`:

- has two tenant variants, Larkspur (a subclass) and Thistle (a variant project), handed out by `PayeeLookupPanelFactory`;
- is embedded by `ClaimPayoutForm` on Load and by `RefundPayoutForm` on a click, and each host sets its inputs;
- calls `svc:IClientService.GetPayeeAccounts` and `svc:IPolicyService.VerifyPayoutAccount`.

`manifests/` is not committed here because it is generated.

- `fixtures/`: two recorded replies, and one synthetic rejection written by hand with `shapeVerified` and a note.
- `registry/`:
  - two scenarios, one per host;
  - the decision behind the one open question (the refund host never sets `AllowThirdParty`);
  - the `closed-account` error case of `VerifyPayoutAccount`.
- `spec/PayeeLookupPanel.md`: a gate-passing spec, with a rule scoped to each variant.

The `"module"` stamps and the spec's `manifest:` are placeholders (`sha256:0000000000000000`). The test
`IssueTests.issue_18_...` writes its extract's revision in their place. To try it:

```sh
bin/parity extract --config fixtures/extract.config.json --module Payouts --forms 'PayeeLookupPanel,ClaimPayoutForm,RefundPayoutForm'
cp -r examples/Payouts/registry examples/Payouts/fixtures examples/Payouts/spec .out/legacy/Payouts/
bin/parity registry check .out/legacy/Payouts            # REGISTRY-STALE (the placeholder stamps)
bin/parity registry stamp .out/legacy/Payouts
bin/parity registry check .out/legacy/Payouts --gate
bin/parity registry catalog .out/legacy/Payouts
sed -i "s/sha256:0000000000000000/$(bin/parity hash .out/legacy/Payouts/manifests/PayeeLookupPanel.json)/" .out/legacy/Payouts/spec/PayeeLookupPanel.md
bin/parity lint .out/legacy/Payouts/spec/PayeeLookupPanel.md --gate   # 0 error(s), 0 warning(s), fixtures: 3 (1 synthetic)
```
