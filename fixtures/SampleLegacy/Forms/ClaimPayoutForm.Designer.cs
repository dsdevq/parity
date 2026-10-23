namespace SampleLegacy.Forms
{
    partial class ClaimPayoutForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.pnlPayee = new DevExpress.XtraEditors.PanelControl();
            this.btnPay = new DevExpress.XtraEditors.SimpleButton();
            this.lblPaidTo = new DevExpress.XtraEditors.LabelControl();
            this.SuspendLayout();
            this.pnlPayee.Name = "pnlPayee";
            this.btnPay.Name = "btnPay";
            this.btnPay.Text = "Pay claim";
            this.btnPay.Click += new System.EventHandler(this.btnPay_Click);
            this.lblPaidTo.Name = "lblPaidTo";
            this.Controls.Add(this.pnlPayee);
            this.Controls.Add(this.btnPay);
            this.Controls.Add(this.lblPaidTo);
            this.Name = "ClaimPayoutForm";
            this.Text = "Claim payout";
            this.Load += new System.EventHandler(this.ClaimPayoutForm_Load);
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.PanelControl pnlPayee;
        private DevExpress.XtraEditors.SimpleButton btnPay;
        private DevExpress.XtraEditors.LabelControl lblPaidTo;
    }
}
