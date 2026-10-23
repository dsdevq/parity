using System;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using SampleLegacy.Forms;
using SampleLegacy.Helpers;

namespace SampleLegacy.Tenants.Thistle
{
    /// <summary>Tenant variant: Thistle selects by double-click only, files clients by family name and has no date of birth.</summary>
    public class ThistleClientSearchForm : ClientSearchForm
    {
        public ThistleClientSearchForm()
        {
            btnSelect.Visible = false;
            lblSurname.Text = "Family name";
            Controls.Remove(dtDob);
            var lblTenant = new LabelControl();
            lblTenant.Text = "Thistle clients only";
            Controls.Add(lblTenant);
            txtPostcode.KeyDown += txtPostcode_KeyDown;
        }

        protected override void btnSearch_Click(object sender, EventArgs e)
        {
            base.btnSearch_Click(sender, e);
            if (gvResults.GetFocusedRow() == null) UiHelper.Warn("No Thistle client matches");
        }

        private void txtPostcode_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) btnSearch_Click(sender, EventArgs.Empty);
        }
    }
}
