---
form: form:PayeeLookupPanel
manifest: sha256:0000000000000000
status: reviewed
reviewed-by: example-reviewer 2026-10-23
---

# PayeeLookupPanel - Payout account

Purpose: lists the accounts a payout can go to and checks the chosen one with the host before the embedding form pays. Embedded by form:ClaimPayoutForm and form:RefundPayoutForm (module.json composition); a factory hands Larkspur and Thistle tenants their own variant.

## Fields

| control | label | kind | binding | disposition | note |
|---|---|---|---|---|---|
| ctrl:PayeeLookupPanel/grdAccounts | Accounts | grid | accounts | field | filled by R-PayeeLookupPanel-001 |
| ctrl:PayeeLookupPanel/gvAccounts | Accounts | grid | accounts | field | view of grdAccounts |
| ctrl:PayeeLookupPanel/colHolder | Account holder | column | accounts.Holder | field | read-only |
| ctrl:PayeeLookupPanel/colIban | IBAN | column | accounts.Iban | field | read-only |
| ctrl:PayeeLookupPanel/btnVerify | Verify account | action | - | field | Larkspur captions it "Check domestic account" (R-PayeeLookupPanel-004) |
| ctrl:PayeeLookupPanel/lblCurrency | Currency | display | - | display | the currency the host passed to Setup |
| ctrl:PayeeLookupPanel/lblStatus | Status | display | - | display | result of the account check |

## Rules

### R-PayeeLookupPanel-001
- kind: data
- covers: evt:PayeeLookupPanel/$this.Setup
- cite: handler:PayeeLookupPanel/Setup
- given: the host has set Purpose and, for third-party payees, AllowThirdParty (the claim host sets it true, the refund host leaves it false per dec:refund-offers-own-accounts-only)
- when: the host calls Setup with a policy id and a currency
- then: lblCurrency shows the currency, grdAccounts is filled from svc:IClientService.GetPayeeAccounts called with the policy id and AllowThirdParty, and btnVerify is disabled
- fixtures: payee-accounts
- scenarios: scn:claim-paid-to-verified-account@1

### R-PayeeLookupPanel-002
- kind: enablement
- covers: evt:PayeeLookupPanel/gvAccounts.FocusedRowChanged
- cite: handler:PayeeLookupPanel/gvAccounts_FocusedRowChanged
- given: grdAccounts is filled
- when: the focused row of gvAccounts changes
- then: btnVerify is enabled exactly when a row is focused, and lblStatus is cleared

### R-PayeeLookupPanel-003
- kind: service
- covers: evt:PayeeLookupPanel/btnVerify.Click
- cite: handler:PayeeLookupPanel/btnVerify_Click, method:SampleLegacy.Helpers.UiHelper.Warn(string)
- given: Setup has run (R-PayeeLookupPanel-001)
- when: btnVerify is clicked
- then: with no focused row a warning "Choose an account" is shown and nothing is called; otherwise svc:IPolicyService.VerifyPayoutAccount is called with the policy id, the focused AccountId and Purpose; on Valid the account becomes SelectedAccount and lblStatus shows "Account verified"; on not Valid lblStatus shows the Reason and SelectedAccount is cleared
- outcomes: valid, rejected
- fixtures: verify-account, verify-account.rejected
- scenarios: scn:claim-paid-to-verified-account@1, scn:refund-to-closed-account-refused@1

### R-PayeeLookupPanel-004
- kind: validation
- variants: Larkspur
- covers: evt:PayeeLookupPanel/btnVerify.Click, var:Larkspur/retagged/btnVerify
- cite: var:Larkspur/override/btnVerify_Click, method:SampleLegacy.Helpers.UiHelper.Warn(string)
- given: the tenant is Larkspur (factory key LRK in method:SampleLegacy.Forms.PayeeLookupPanelFactory.For(string))
- when: btnVerify, captioned "Check domestic account", is clicked with a focused account that is not Domestic
- then: a warning "Larkspur pays to domestic accounts only" is shown and svc:IPolicyService.VerifyPayoutAccount is not called; a domestic account or no focused row goes on as R-PayeeLookupPanel-003

### R-PayeeLookupPanel-005
- kind: ui
- variants: Thistle
- covers: evt:PayeeLookupPanel/$this.Setup, var:Thistle/retagged/lblCurrency
- cite: handler:PayeeLookupPanel/Setup
- given: the tenant is Thistle (factory key THS in method:SampleLegacy.Forms.PayeeLookupPanelFactory.For(string))
- when: the panel is shown
- then: lblCurrency is hidden; everything else is as R-PayeeLookupPanel-001

## No behavior

| event | reason |
|---|---|
| evt:PayeeLookupPanel/$this.ctor | the constructor only builds the designer controls |

## Service calls

| service | called from | fixtures |
|---|---|---|
| svc:IClientService.GetPayeeAccounts | R-PayeeLookupPanel-001 | payee-accounts |
| svc:IPolicyService.VerifyPayoutAccount | R-PayeeLookupPanel-003 | verify-account, verify-account.rejected |

## Open questions

- closed by decision dec:refund-offers-own-accounts-only: the refund host never sets AllowThirdParty; does a refund offer third-party accounts?
- none
