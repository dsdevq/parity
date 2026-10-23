using System;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;
using SampleLegacy.Services;

namespace SampleLegacy.Forms
{
    public partial class ClaimClientSearchForm : XtraForm
    {
        public ClientDto SelectedClient { get; private set; }

        public ClaimClientSearchForm()
        {
            InitializeComponent();
        }

        private void btnSearch_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtSurname.Text))
            {
                UiHelper.Warn("Surname is required");
                return;
            }
            var criteria = new ClientSearchCriteria
            {
                Surname = txtSurname.Text,
                Postcode = txtPostcode.Text,
                DateOfBirth = dtDob.EditValue as DateTime?,
                IncludeDeceased = false
            };
            grdResults.DataSource = ServiceLocator.Client.SearchClients(criteria);
            btnSelect.Enabled = false;
        }

        private void txtSurname_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) btnSearch_Click(sender, EventArgs.Empty);
        }

        private void gvResults_FocusedRowChanged(object sender, EventArgs e)
        {
            btnSelect.Enabled = gvResults.GetFocusedRow() != null;
        }

        private void gvResults_DoubleClick(object sender, EventArgs e)
        {
            btnSelect_Click(sender, e);
        }

        private void btnSelect_Click(object sender, EventArgs e)
        {
            SelectedClient = gvResults.GetFocusedRow() as ClientDto;
            if (SelectedClient == null) return;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
