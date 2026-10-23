using System;
using SampleLegacy.Forms;
using SampleLegacy.Helpers;
using SampleLegacy.Services;

namespace SampleLegacy.Tenants.Larkspur
{
    /// <summary>Tenant variant: Larkspur pays only to domestic accounts, and refuses the others before asking the service.</summary>
    public class LarkspurPayeeLookupPanel : PayeeLookupPanel
    {
        public LarkspurPayeeLookupPanel()
        {
            btnVerify.Text = "Check domestic account";
        }

        protected override void btnVerify_Click(object sender, EventArgs e)
        {
            var account = gvAccounts.GetFocusedRow() as PayeeAccountDto;
            if (account != null && !account.Domestic)
            {
                UiHelper.Warn("Larkspur pays to domestic accounts only");
                return;
            }
            base.btnVerify_Click(sender, e);
        }
    }
}
