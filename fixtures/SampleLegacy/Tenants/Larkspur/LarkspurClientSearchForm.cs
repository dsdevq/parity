using System;
using SampleLegacy.Forms;
using SampleLegacy.Helpers;
using SampleLegacy.Services;

namespace SampleLegacy.Tenants.Larkspur
{
    /// <summary>Tenant variant: Larkspur searches its loyalty book instead of the client register.</summary>
    public partial class LarkspurClientSearchForm : ClientSearchForm
    {
        public LarkspurClientSearchForm()
        {
            InitializeComponent();
        }

        protected override void btnSearch_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtSurname.Text) && string.IsNullOrEmpty(txtPostcode.Text))
            {
                UiHelper.Warn("Enter a surname or a zip code");
                return;
            }
            var criteria = new ClientSearchCriteria
            {
                Surname = txtSurname.Text,
                Postcode = txtPostcode.Text
            };
            grdResults.DataSource = ServiceLocator.Client.SearchLoyaltyClients(criteria, chkLoyaltyOnly.Checked);
            btnSelect.Enabled = false;
        }

        private void chkLoyaltyOnly_CheckedChanged(object sender, EventArgs e)
        {
            grdResults.DataSource = null;
            btnSelect.Enabled = false;
        }
    }
}
