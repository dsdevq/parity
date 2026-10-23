using System;
using System.ComponentModel;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;
using SampleLegacy.Services;

namespace SampleLegacy.Forms
{
    public partial class PolicyWizardStep3 : XtraForm
    {
        private PolicyDto _policy;
        private PremiumLimitsDto _limits;
        private readonly PolicyWizardController _controller;
        private bool _installmentsWereVisible;

        public PolicyWizardStep3(PolicyWizardController controller, PolicyDto policy)
        {
            InitializeComponent();
            _controller = controller;
            _policy = policy;
            policyBindingSource.DataSource = _policy;
            // code-behind wiring the designer never sees
            this.btnNext.EnabledChanged += BtnNext_EnabledChanged;
            this.dtStart.EditValueChanged += (s, e) => { if (dtStart.DateTime < DateTime.Today) errorProvider.SetError(dtStart, "Start date cannot be in the past"); };
            btnBack.Enabled = _controller.CanGoBack;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            cboProduct.Properties.DataSource = ServiceLocator.Policy.GetProducts();
            RecalculateTotal();
        }

        private void PolicyWizardStep3_Load(object sender, EventArgs e)
        {
            txtInstallments.Visible = !chkSinglePremium.Checked;
            _installmentsWereVisible = txtInstallments.Visible;
        }

        private void cboProduct_EditValueChanged(object sender, EventArgs e)
        {
            if (cboProduct.EditValue == null) { _limits = null; return; }
            var productId = (int)cboProduct.EditValue;
            _limits = ServiceLocator.Policy.GetPremiumLimits(productId);
            grdRiders.DataSource = ServiceLocator.Policy.GetAvailableRiders(productId);
            txtPremium.Enabled = true;
        }

        private void txtPremium_Enter(object sender, EventArgs e)
        {
            txtPremium.SelectAll();
        }

        private void txtPremium_EditValueChanged(object sender, EventArgs e)
        {
            RecalculateTotal();
        }

        private void chkSinglePremium_CheckedChanged(object sender, EventArgs e)
        {
            if (chkSinglePremium.Checked)
            {
                cboFrequency.Enabled = false;
                cboFrequency.EditValue = "Annual";
                // quirk: only cleared when it was previously visible
                if (_installmentsWereVisible) txtInstallments.EditValue = 1;
                txtInstallments.Visible = false;
            }
            else
            {
                cboFrequency.Enabled = true;
                txtInstallments.Visible = true;
            }
            _installmentsWereVisible = txtInstallments.Visible;
        }

        private void cboFrequency_SelectedIndexChanged(object sender, EventArgs e)
        {
            txtInstallments.EditValue = PremiumRules.DefaultInstallments(cboFrequency.EditValue as string);
        }

        private void gvRiders_CellValueChanged(object sender, EventArgs e)
        {
            RecalculateTotal();
        }

        private void btnNext_Click(object sender, EventArgs e)
        {
            if (!validationProvider.Validate()) return;
            if (!ValidatePremiumAgainstLimits()) return;
            ServiceLocator.Policy.SavePolicyStep(_policy, 3);
            _controller.Next();
        }

        private void btnBack_Click(object sender, EventArgs e)
        {
            _controller.Back();
        }

        private void btnLegacyPrint_Click(object sender, EventArgs e)
        {
            UiHelper.Warn("Printing is not available in this environment");
        }

        private void BtnNext_EnabledChanged(object sender, EventArgs e)
        {
            lblTotal.Visible = btnNext.Enabled;
        }

        private void RecalculateTotal()
        {
            decimal premium = _policy.Premium;
            if (_policy.Riders != null)
            {
                foreach (var rider in _policy.Riders)
                    premium = PremiumRules.ApplyLoading(premium, rider.Loading);
            }
            lblTotal.Text = "Total: " + premium.ToString("0.00");
        }
    }
}
