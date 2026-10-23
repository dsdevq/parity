using System;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;

namespace SampleLegacy.Forms
{
    // Pays a claim: embeds the tenant's payee panel when it opens, offering third-party accounts, in euros,
    // and pays to the account the panel verified.
    public partial class ClaimPayoutForm : XtraForm
    {
        private PayeeLookupPanel _payee;

        public ClaimPayoutForm()
        {
            InitializeComponent();
        }

        public string TenantCode { get; set; }

        public int ClaimPolicyId { get; set; }

        private void ClaimPayoutForm_Load(object sender, EventArgs e)
        {
            _payee = PayeeLookupPanelFactory.For(TenantCode);
            _payee.Purpose = "claim";
            _payee.AllowThirdParty = true;
            _payee.Setup(ClaimPolicyId, "EUR");
            pnlPayee.Controls.Add(_payee);
        }

        private void btnPay_Click(object sender, EventArgs e)
        {
            if (_payee.SelectedAccount == null)
            {
                UiHelper.Warn("Verify the payee account first");
                return;
            }
            lblPaidTo.Text = _payee.SelectedAccount.Holder;
        }
    }
}
