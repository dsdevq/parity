using System;
using System.Windows.Forms;
using SampleLegacy.Services;

namespace SampleLegacy.Forms
{
    public partial class BeneficiarySearchForm : BaseDialogForm
    {
        public ClientDto Selected { get; private set; }
        private bool _allowDeceased;
        private int _policyId;

        // Entry point: hosting forms call this to hand the dialog its inputs. Never wired to an event.
        public void Setup(int policyId, bool allowDeceased, ClientDto preselected)
        {
            _policyId = policyId;
            _allowDeceased = allowDeceased;
            if (preselected != null) txtName.EditValue = preselected.Surname;
            InitHistoryColumns();
        }

        private void InitHistoryColumns()
        {
            gvHistory.Columns.AddVisible("EventDate", "Date");
            gvHistory.Columns.AddVisible("Description");
        }

        public BeneficiarySearchForm()
        {
            // VB6-upgrade style: column headers created in code, not in the designer
            _lstResults_ColumnHeader_1 = new ColumnHeader("name");
            _lstResults_ColumnHeader_2 = new ColumnHeader("dob");
            InitializeComponent();
        }

        private void txtName_ButtonClick(object sender, EventArgs e)
        {
            lstResults.Columns[0].Text = "Name";
            var results = ServiceLocator.Client.SearchClients(new ClientSearchCriteria { Surname = txtName.EditValue as string, IncludeDeceased = _allowDeceased });
            btnOK.Enabled = results.Count > 0;
        }

        private void lstResults_SelectedIndexChanged(object sender, EventArgs e)
        {
            btnOK.Enabled = true;
        }

        protected override void OnAccept()
        {
            Selected = new ClientDto();
            DialogResult = DialogResult.OK;
            Close();
        }

        // handler-shaped, never wired anywhere: must be diagnosed as such
        private void btnExport_Click(object sender, EventArgs e)
        {
            MessageBox.Show("export");
        }
    }
}
