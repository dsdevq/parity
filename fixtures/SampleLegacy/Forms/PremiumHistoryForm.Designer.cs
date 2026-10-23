namespace SampleLegacy.Forms
{
    partial class PremiumHistoryForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.bsHistory = new System.Windows.Forms.BindingSource(this.components);
            this.grdHistory = new DevExpress.XtraGrid.GridControl();
            this.gvHistory = new DevExpress.XtraGrid.Views.Grid.GridView();
            this.colEffectiveDate = new DevExpress.XtraGrid.Columns.GridColumn();
            this.colReason = new DevExpress.XtraGrid.Columns.GridColumn();
            this.colAmount = new DevExpress.XtraGrid.Columns.GridColumn();
            this.lblReasons = new DevExpress.XtraEditors.LabelControl();
            this.lblStatus = new DevExpress.XtraEditors.LabelControl();
            this.btnArchive = new DevExpress.XtraEditors.SimpleButton();
            this.SuspendLayout();
            this.bsHistory.DataSource = typeof(SampleLegacy.Models.PremiumHistoryRow);
            this.grdHistory.DataSource = this.bsHistory;
            this.grdHistory.MainView = this.gvHistory;
            this.grdHistory.Name = "grdHistory";
            this.gvHistory.Columns.AddRange(new DevExpress.XtraGrid.Columns.GridColumn[] { this.colEffectiveDate, this.colReason, this.colAmount });
            this.gvHistory.GridControl = this.grdHistory;
            this.gvHistory.Name = "gvHistory";
            this.colEffectiveDate.FieldName = "EffectiveDate";
            this.colEffectiveDate.Visible = true;
            this.colReason.FieldName = "Reason";
            this.colReason.Visible = true;
            this.colAmount.FieldName = "Amount";
            this.colAmount.Visible = true;
            this.lblReasons.Name = "lblReasons";
            this.lblStatus.Name = "lblStatus";
            this.btnArchive.Name = "btnArchive";
            this.btnArchive.Text = "Archive";
            this.btnArchive.Click += new System.EventHandler(this.btnArchive_Click);
            this.Controls.Add(this.grdHistory);
            this.Controls.Add(this.lblReasons);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnArchive);
            this.Name = "PremiumHistoryForm";
            this.Load += new System.EventHandler(this.PremiumHistoryForm_Load);
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.BindingSource bsHistory;
        private DevExpress.XtraGrid.GridControl grdHistory;
        private DevExpress.XtraGrid.Views.Grid.GridView gvHistory;
        private DevExpress.XtraGrid.Columns.GridColumn colEffectiveDate;
        private DevExpress.XtraGrid.Columns.GridColumn colReason;
        private DevExpress.XtraGrid.Columns.GridColumn colAmount;
        private DevExpress.XtraEditors.LabelControl lblReasons;
        private DevExpress.XtraEditors.LabelControl lblStatus;
        private DevExpress.XtraEditors.SimpleButton btnArchive;
    }
}
