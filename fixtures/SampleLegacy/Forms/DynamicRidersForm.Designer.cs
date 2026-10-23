namespace SampleLegacy.Forms
{
    partial class DynamicRidersForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.pnlRiders = new DevExpress.XtraEditors.PanelControl();
            this.btnApply = new DevExpress.XtraEditors.SimpleButton();
            this.SuspendLayout();
            this.pnlRiders.Name = "pnlRiders";
            this.pnlRiders.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnApply.Name = "btnApply";
            this.btnApply.Text = "Apply";
            this.btnApply.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.btnApply.Click += new System.EventHandler(this.btnApply_Click);
            this.Controls.Add(this.pnlRiders);
            this.Controls.Add(this.btnApply);
            this.Name = "DynamicRidersForm";
            this.Text = "Riders";
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.PanelControl pnlRiders;
        private DevExpress.XtraEditors.SimpleButton btnApply;
    }
}
