namespace SampleLegacy.Forms
{
    partial class BeneficiaryHostForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.btnChoose = new DevExpress.XtraEditors.SimpleButton();
            this.btnSummary = new DevExpress.XtraEditors.SimpleButton();
            this.btnFindClient = new DevExpress.XtraEditors.SimpleButton();
            this.lblChosen = new DevExpress.XtraEditors.LabelControl();
            this.SuspendLayout();
            this.btnChoose.Name = "btnChoose";
            this.btnChoose.Text = "Choose beneficiary";
            this.btnChoose.Click += new System.EventHandler(this.btnChoose_Click);
            this.btnSummary.Name = "btnSummary";
            this.btnSummary.Text = "Summary";
            this.btnSummary.Click += new System.EventHandler(this.btnSummary_Click);
            this.btnFindClient.Name = "btnFindClient";
            this.btnFindClient.Text = "Find client";
            this.btnFindClient.Click += new System.EventHandler(this.btnFindClient_Click);
            this.lblChosen.Name = "lblChosen";
            this.Controls.Add(this.btnChoose);
            this.Controls.Add(this.btnSummary);
            this.Controls.Add(this.btnFindClient);
            this.Controls.Add(this.lblChosen);
            this.Name = "BeneficiaryHostForm";
            this.Text = "Beneficiary";
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.SimpleButton btnChoose;
        private DevExpress.XtraEditors.SimpleButton btnSummary;
        private DevExpress.XtraEditors.SimpleButton btnFindClient;
        private DevExpress.XtraEditors.LabelControl lblChosen;
    }
}
