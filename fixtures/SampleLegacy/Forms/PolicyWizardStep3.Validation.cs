using System.ComponentModel;
using SampleLegacy.Helpers;

namespace SampleLegacy.Forms
{
    // third partial: validation lives apart from the handlers, as it does in the real app
    public partial class PolicyWizardStep3
    {
        private void txtPremium_Validating(object sender, CancelEventArgs e)
        {
            if (!ValidatePremiumAgainstLimits())
            {
                e.Cancel = true;
                btnNext.Enabled = false;
            }
            else
            {
                errorProvider.ClearErrors();
                btnNext.Enabled = true;
            }
        }

        private bool ValidatePremiumAgainstLimits()
        {
            decimal premium;
            if (!decimal.TryParse(System.Convert.ToString(txtPremium.EditValue), out premium)) return false;
            if (!PremiumRules.IsWithinLimits(premium, _limits))
            {
                errorProvider.SetError(txtPremium, "Premium exceeds product maximum");
                return false;
            }
            return true;
        }
    }
}
