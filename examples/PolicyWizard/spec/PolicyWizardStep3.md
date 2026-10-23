---
form: form:PolicyWizardStep3
manifest: sha256:0000000000000000
status: reviewed
reviewed-by: example-reviewer 2026-10-22
---

# PolicyWizardStep3 - Premium and payment

Purpose: third step of the new-business wizard. Captures product, premium, payment frequency and riders, validates the premium against the product limits, and saves the step.

## Fields

| control | label | kind | binding | disposition | note |
|---|---|---|---|---|---|
| ctrl:PolicyWizardStep3/cboProduct | Product | input | policy.ProductId | field | lookup over svc:IPolicyService.GetProducts, display Name, value ProductId |
| ctrl:PolicyWizardStep3/txtPremium | Premium | input | policy.Premium | field | currency mask c2 |
| ctrl:PolicyWizardStep3/chkSinglePremium | Single premium | input | policy.SinglePremium | field | |
| ctrl:PolicyWizardStep3/cboFrequency | Frequency | input | policy.Frequency | field | items Annual, Quarterly, Monthly |
| ctrl:PolicyWizardStep3/txtInstallments | Installments | input | policy.Installments | field | |
| ctrl:PolicyWizardStep3/dtStart | Start date | input | policy.StartDate | field | |
| ctrl:PolicyWizardStep3/grdRiders | Riders | grid | policy.Riders | shared:RiderGrid | |
| ctrl:PolicyWizardStep3/gvRiders | Riders | grid | policy.Riders | shared:RiderGrid | view of grdRiders |
| ctrl:PolicyWizardStep3/colRiderName | Rider | column | Riders.Name | field | read-only |
| ctrl:PolicyWizardStep3/colLoading | Loading % | column | Riders.Loading | field | editable |
| ctrl:PolicyWizardStep3/lblTotal | Total | display | - | display | computed, see R-PolicyWizardStep3-005 |
| ctrl:PolicyWizardStep3/btnNext | Next > | action | - | field | |
| ctrl:PolicyWizardStep3/btnBack | < Back | action | - | field | |
| ctrl:PolicyWizardStep3/btnLegacyPrint | Print | action | - | dropped | hidden in legacy (Visible=false) and printing is out of scope for phase 1, decided-by example-reviewer 2026-10-22 |

## Rules

### R-PolicyWizardStep3-001
- kind: validation
- covers: evt:PolicyWizardStep3/txtPremium.Validating
- cite: handler:PolicyWizardStep3/txtPremium_Validating, method:SampleLegacy.Forms.PolicyWizardStep3.ValidatePremiumAgainstLimits(), method:SampleLegacy.Helpers.PremiumRules.IsWithinLimits(decimal, SampleLegacy.Services.PremiumLimitsDto)
- given: a product is selected and its limits were loaded by R-PolicyWizardStep3-003
- when: txtPremium loses focus with a value that does not parse as a decimal, or is below Min, or above Max
- then: txtPremium shows the error "Premium exceeds product maximum", focus stays on txtPremium, btnNext is disabled; when the value is within limits the error is cleared and btnNext is enabled
- fixtures: limits-term-life

### R-PolicyWizardStep3-002
- kind: enablement
- covers: evt:PolicyWizardStep3/chkSinglePremium.CheckedChanged
- cite: handler:PolicyWizardStep3/chkSinglePremium_CheckedChanged
- given: any state
- when: chkSinglePremium becomes checked
- then: cboFrequency is disabled and set to "Annual", txtInstallments is hidden; when unchecked cboFrequency is enabled and txtInstallments is shown
- quirk: preserve - txtInstallments is reset to 1 only if it was visible before the click (field _installmentsWereVisible), decided-by example-reviewer 2026-10-22

### R-PolicyWizardStep3-003
- kind: data
- covers: evt:PolicyWizardStep3/cboProduct.EditValueChanged
- cite: handler:PolicyWizardStep3/cboProduct_EditValueChanged
- given: the product lookup has items
- when: cboProduct changes to a product
- then: svc:IPolicyService.GetPremiumLimits is called with the ProductId and the result is kept for R-PolicyWizardStep3-001, svc:IPolicyService.GetAvailableRiders is called with the ProductId and fills grdRiders, txtPremium becomes enabled; when cboProduct is cleared the limits are discarded and nothing is called
- fixtures: limits-term-life, riders-term-life

### R-PolicyWizardStep3-004
- kind: calculation
- covers: evt:PolicyWizardStep3/cboFrequency.SelectedIndexChanged
- cite: handler:PolicyWizardStep3/cboFrequency_SelectedIndexChanged, method:SampleLegacy.Helpers.PremiumRules.DefaultInstallments(string)
- given: any state
- when: cboFrequency changes
- then: txtInstallments is set to 12 for Monthly, 4 for Quarterly, 1 for anything else

### R-PolicyWizardStep3-005
- kind: calculation
- covers: evt:PolicyWizardStep3/txtPremium.EditValueChanged, evt:PolicyWizardStep3/gvRiders.CellValueChanged
- cite: handler:PolicyWizardStep3/txtPremium_EditValueChanged, handler:PolicyWizardStep3/gvRiders_CellValueChanged, method:SampleLegacy.Forms.PolicyWizardStep3.RecalculateTotal(), method:SampleLegacy.Helpers.PremiumRules.ApplyLoading(decimal, decimal)
- given: the policy has a premium and zero or more riders with a Loading percent
- when: txtPremium changes or a rider cell changes
- then: lblTotal shows "Total: " followed by the premium with every rider loading applied in sequence (premium + premium * loading / 100, compounding in rider order), formatted 0.00

### R-PolicyWizardStep3-006
- kind: navigation
- covers: evt:PolicyWizardStep3/btnNext.Click
- cite: handler:PolicyWizardStep3/btnNext_Click, method:SampleLegacy.Forms.PolicyWizardController.Next(), method:SampleLegacy.Forms.PolicyWizardController.Show(int)
- given: the provider rule "Premium is required" and R-PolicyWizardStep3-001 both pass
- when: btnNext is clicked
- then: svc:IPolicyService.SavePolicyStep is called with the policy and step 3, then the controller advances: to step 5 when the policy is single premium, otherwise to step 4 (form:DynamicRidersForm); when either validation fails nothing is saved and the form stays
- fixtures: save-step3

### R-PolicyWizardStep3-007
- kind: navigation
- covers: evt:PolicyWizardStep3/btnBack.Click
- cite: handler:PolicyWizardStep3/btnBack_Click, method:SampleLegacy.Forms.PolicyWizardController.Back()
- given: btnBack is enabled (R-PolicyWizardStep3-010)
- when: btnBack is clicked
- then: the controller moves to step 3 from step 5 for single-premium policies, otherwise one step back, and shows that step's form (form:PolicyWizardStep3 for step 3, form:DynamicRidersForm for step 4)

### R-PolicyWizardStep3-008
- kind: lifecycle
- covers: evt:PolicyWizardStep3/$this.OnLoad
- cite: handler:PolicyWizardStep3/OnLoad
- given: the form is opening
- when: OnLoad runs
- then: cboProduct is filled from svc:IPolicyService.GetProducts and lblTotal is recalculated per R-PolicyWizardStep3-005
- fixtures: products

### R-PolicyWizardStep3-009
- kind: lifecycle
- covers: evt:PolicyWizardStep3/$this.Load
- cite: handler:PolicyWizardStep3/PolicyWizardStep3_Load
- given: the form is opening
- when: the Load event fires
- then: txtInstallments is visible exactly when chkSinglePremium is unchecked, and that visibility is remembered for the quirk in R-PolicyWizardStep3-002

### R-PolicyWizardStep3-010
- kind: lifecycle
- covers: evt:PolicyWizardStep3/$this.ctor(2)
- cite: handler:PolicyWizardStep3/ctor(2), method:SampleLegacy.Forms.PolicyWizardController.CanGoBack.get
- given: the form is constructed with a controller and a policy
- when: construction completes
- then: policyBindingSource is bound to the policy, btnBack is enabled only when the controller's current step is greater than 1

### R-PolicyWizardStep3-011
- kind: validation
- covers: evt:PolicyWizardStep3/dtStart.EditValueChanged
- cite: handler:PolicyWizardStep3/lambda@L25
- given: any state
- when: dtStart changes to a date before today
- then: dtStart shows the error "Start date cannot be in the past"; a date today or later sets no error and clears nothing

### R-PolicyWizardStep3-012
- kind: ui
- covers: evt:PolicyWizardStep3/btnNext.EnabledChanged
- cite: handler:PolicyWizardStep3/BtnNext_EnabledChanged
- given: any state
- when: btnNext changes enabled state
- then: lblTotal is visible exactly when btnNext is enabled

## No behavior

| event | reason |
|---|---|
| evt:PolicyWizardStep3/txtPremium.Enter | handler only selects the text |
| evt:PolicyWizardStep3/btnLegacyPrint.Click | control is dropped (see Fields); legacy shows a "not available" warning |

## Service calls

| service | called from | fixtures |
|---|---|---|
| svc:IPolicyService.GetPremiumLimits | R-PolicyWizardStep3-003 | limits-term-life |
| svc:IPolicyService.GetAvailableRiders | R-PolicyWizardStep3-003 | riders-term-life |
| svc:IPolicyService.GetProducts | R-PolicyWizardStep3-008 | products |
| svc:IPolicyService.SavePolicyStep | R-PolicyWizardStep3-006 | save-step3 |

## Open questions

- none
