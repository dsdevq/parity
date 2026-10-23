using System;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;
using SampleLegacy.Services;
using SampleLegacy.Session;

namespace SampleLegacy.Forms
{
    // Reads what the host form left in the shared session and sends it to the policy service.
    public partial class CoverSummaryForm : XtraForm
    {
        private bool _defaultsApplied;

        public CoverSummaryForm()
        {
            InitializeComponent();
        }

        // never called by name in code: the host invokes it through reflection
        private void ApplyDefaults()
        {
            _defaultsApplied = true;
            lblBeneficiary.Text = WizardSession.Current.BeneficiaryName;
        }

        private void btnConfirm_Click(object sender, EventArgs e)
        {
            var session = WizardSession.Current;
            if (session.BeneficiaryId == 0) { UiHelper.Warn("Choose a beneficiary first"); return; }
            ServiceLocator.Policy.AddBeneficiary(session.PolicyId, session.BeneficiaryId);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
