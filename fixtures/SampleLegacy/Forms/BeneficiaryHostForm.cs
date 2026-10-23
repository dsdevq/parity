using System;
using System.Reflection;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using SampleLegacy.Services;
using SampleLegacy.Session;

namespace SampleLegacy.Forms
{
    // Hosts the beneficiary search: hands it its inputs through Setup, then keeps the choice in the shared session.
    public partial class BeneficiaryHostForm : XtraForm
    {
        public BeneficiaryHostForm()
        {
            InitializeComponent();
        }

        // set by whoever opens this form, passed on to the search
        public bool AllowDeceased { get; set; }

        // picks which tenant's client search the factory hands out
        public string TenantCode { get; set; }

        private void btnChoose_Click(object sender, EventArgs e)
        {
            var search = new BeneficiarySearchForm();
            search.Setup(WizardSession.Current.PolicyId, AllowDeceased, null);
            if (search.ShowDialog() == DialogResult.OK) StoreChoice(search.Selected);
        }

        private void StoreChoice(ClientDto chosen)
        {
            var session = WizardSession.Current;
            session.BeneficiaryId = chosen.ClientId;
            session.BeneficiaryName = chosen.Surname;
            session.Changes++;
            lblChosen.Text = chosen.Surname;
        }

        private void btnSummary_Click(object sender, EventArgs e)
        {
            var summary = new CoverSummaryForm();
            // the summary's defaults are applied by name, the way the old plug-in loader did it
            typeof(CoverSummaryForm).GetMethod("ApplyDefaults", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(summary, null);
            summary.ShowDialog();
        }

        private void btnFindClient_Click(object sender, EventArgs e)
        {
            var finder = ClientSearchFormFactory.Create(TenantCode);
            finder.ShowDialog();
        }
    }
}
