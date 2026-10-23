using System;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;
using SampleLegacy.Services;

namespace SampleLegacy.Forms
{
    // Picks and checks the account a payout goes to. Two hosts embed it (#18): each sets what the payout is for
    // and whether third-party accounts are offered, then calls Setup with the policy and the currency.
    public partial class PayeeLookupPanel : XtraUserControl
    {
        private int _policyId;

        public PayeeLookupPanel()
        {
            InitializeComponent();
        }

        // claim or refund: the account check applies different rules to each
        public string Purpose { get; set; }

        // read by Setup, so a host sets it first
        public bool AllowThirdParty { get; set; }

        public PayeeAccountDto SelectedAccount { get; private set; }

        public void Setup(int policyId, string currency)
        {
            _policyId = policyId;
            lblCurrency.Text = currency;
            grdAccounts.DataSource = ServiceLocator.Client.GetPayeeAccounts(policyId, AllowThirdParty);
            btnVerify.Enabled = false;
        }

        protected virtual void btnVerify_Click(object sender, EventArgs e)
        {
            var account = gvAccounts.GetFocusedRow() as PayeeAccountDto;
            if (account == null)
            {
                UiHelper.Warn("Choose an account");
                return;
            }
            var check = ServiceLocator.Policy.VerifyPayoutAccount(_policyId, account.AccountId, Purpose);
            if (!check.Valid)
            {
                lblStatus.Text = check.Reason;
                SelectedAccount = null;
                return;
            }
            SelectedAccount = account;
            lblStatus.Text = "Account verified";
        }

        private void gvAccounts_FocusedRowChanged(object sender, EventArgs e)
        {
            btnVerify.Enabled = gvAccounts.GetFocusedRow() != null;
            lblStatus.Text = "";
        }
    }
}
