namespace SampleLegacy.Tenants.Larkspur
{
    partial class LarkspurClientSearchForm
    {
        private void InitializeComponent()
        {
            this.chkLoyaltyOnly = new DevExpress.XtraEditors.CheckEdit();
            this.SuspendLayout();
            this.chkLoyaltyOnly.Name = "chkLoyaltyOnly";
            this.chkLoyaltyOnly.Properties.Caption = "Loyalty members only";
            this.chkLoyaltyOnly.TabIndex = 7;
            this.chkLoyaltyOnly.CheckedChanged += new System.EventHandler(this.chkLoyaltyOnly_CheckedChanged);
            this.lblPostcode.Text = "Zip code";
            this.Controls.Add(this.chkLoyaltyOnly);
            this.Name = "LarkspurClientSearchForm";
            this.Text = "Client Search - Larkspur";
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.CheckEdit chkLoyaltyOnly;
    }
}
