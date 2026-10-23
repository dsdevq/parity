namespace SampleLegacy.Forms
{
    partial class RefundPayoutForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.btnChoosePayee = new DevExpress.XtraEditors.SimpleButton();
            this.btnRefund = new DevExpress.XtraEditors.SimpleButton();
            this.SuspendLayout();
            this.btnChoosePayee.Name = "btnChoosePayee";
            this.btnChoosePayee.Text = "Choose payee";
            this.btnChoosePayee.Click += new System.EventHandler(this.btnChoosePayee_Click);
            this.btnRefund.Name = "btnRefund";
            this.btnRefund.Text = "Refund";
            this.btnRefund.Click += new System.EventHandler(this.btnRefund_Click);
            this.Controls.Add(this.btnChoosePayee);
            this.Controls.Add(this.btnRefund);
            this.Name = "RefundPayoutForm";
            this.Text = "Premium refund";
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.SimpleButton btnChoosePayee;
        private DevExpress.XtraEditors.SimpleButton btnRefund;
    }
}
