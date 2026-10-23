namespace SampleLegacy.Forms
{
    partial class CoverSummaryForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.lblBeneficiary = new DevExpress.XtraEditors.LabelControl();
            this.btnConfirm = new DevExpress.XtraEditors.SimpleButton();
            this.SuspendLayout();
            this.lblBeneficiary.Name = "lblBeneficiary";
            this.btnConfirm.Name = "btnConfirm";
            this.btnConfirm.Text = "Confirm";
            this.btnConfirm.Click += new System.EventHandler(this.btnConfirm_Click);
            this.Controls.Add(this.lblBeneficiary);
            this.Controls.Add(this.btnConfirm);
            this.Name = "CoverSummaryForm";
            this.Text = "Cover summary";
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.LabelControl lblBeneficiary;
        private DevExpress.XtraEditors.SimpleButton btnConfirm;
    }
}
