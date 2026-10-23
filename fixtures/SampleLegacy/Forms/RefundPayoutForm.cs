using System;
using DevExpress.XtraEditors;

namespace SampleLegacy.Forms
{
    // Refunds a premium: embeds the tenant's payee panel on demand, own accounts only, in the policy's currency.
    public partial class RefundPayoutForm : XtraForm
    {
        private PayeeLookupPanel _refundPayee;

        public RefundPayoutForm()
        {
            InitializeComponent();
        }

        public string TenantCode { get; set; }

        public int PolicyId { get; set; }

        public string PolicyCurrency { get; set; }

        private void btnChoosePayee_Click(object sender, EventArgs e)
        {
            _refundPayee = PayeeLookupPanelFactory.For(TenantCode);
            _refundPayee.Purpose = "refund";
            _refundPayee.Setup(PolicyId, PolicyCurrency);
            Controls.Add(_refundPayee);
            btnChoosePayee.Enabled = false;
        }

        private void btnRefund_Click(object sender, EventArgs e)
        {
            btnRefund.Enabled = _refundPayee != null && _refundPayee.SelectedAccount != null;
        }
    }
}
