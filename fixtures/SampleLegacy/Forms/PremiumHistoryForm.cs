using System;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;
using SampleLegacy.Models;
using SampleLegacy.Resources;

namespace SampleLegacy.Forms
{
    public partial class PremiumHistoryForm : XtraForm
    {
        public PremiumHistoryForm()
        {
            InitializeComponent();
        }

        private void PremiumHistoryForm_Load(object sender, EventArgs e)
        {
            lblReasons.Text = Captions.lsReasons;
            Text = Captions.ResourceManager.GetString("lsHistoryTitle");
        }

        private void btnArchive_Click(object sender, EventArgs e)
        {
            var row = gvHistory.GetFocusedRow() as PremiumHistoryRow;
            if (row == null) return;
            if (row.Reason != null && row.Reason.Length > PolicyLimits.ReasonMaxLength)
            {
                UiHelper.Warn(Captions.ResourceManager.GetString("lsArchiveReasonTooLong"));
                return;
            }
            lblStatus.Text = PolicyLimits.HistoryCategory;
        }
    }
}
