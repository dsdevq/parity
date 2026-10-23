namespace SampleLegacy.Forms
{
    partial class PayeeLookupPanel
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.grdAccounts = new DevExpress.XtraGrid.GridControl();
            this.gvAccounts = new DevExpress.XtraGrid.Views.Grid.GridView();
            this.colHolder = new DevExpress.XtraGrid.Columns.GridColumn();
            this.colIban = new DevExpress.XtraGrid.Columns.GridColumn();
            this.btnVerify = new DevExpress.XtraEditors.SimpleButton();
            this.lblCurrency = new DevExpress.XtraEditors.LabelControl();
            this.lblStatus = new DevExpress.XtraEditors.LabelControl();
            this.SuspendLayout();
            this.grdAccounts.MainView = this.gvAccounts;
            this.grdAccounts.Name = "grdAccounts";
            this.grdAccounts.TabIndex = 0;
            this.gvAccounts.Columns.AddRange(new DevExpress.XtraGrid.Columns.GridColumn[] { this.colHolder, this.colIban });
            this.gvAccounts.GridControl = this.grdAccounts;
            this.gvAccounts.Name = "gvAccounts";
            this.gvAccounts.FocusedRowChanged += new System.EventHandler(this.gvAccounts_FocusedRowChanged);
            this.colHolder.FieldName = "Holder";
            this.colHolder.Caption = "Account holder";
            this.colHolder.Visible = true;
            this.colIban.FieldName = "Iban";
            this.colIban.Caption = "IBAN";
            this.colIban.Visible = true;
            this.btnVerify.Name = "btnVerify";
            this.btnVerify.Text = "Verify account";
            this.btnVerify.TabIndex = 1;
            this.btnVerify.Click += new System.EventHandler(this.btnVerify_Click);
            this.lblCurrency.Name = "lblCurrency";
            this.lblStatus.Name = "lblStatus";
            this.Controls.Add(this.grdAccounts);
            this.Controls.Add(this.btnVerify);
            this.Controls.Add(this.lblCurrency);
            this.Controls.Add(this.lblStatus);
            this.Name = "PayeeLookupPanel";
            this.ResumeLayout(false);
        }

        protected DevExpress.XtraGrid.GridControl grdAccounts;
        protected DevExpress.XtraGrid.Views.Grid.GridView gvAccounts;
        protected DevExpress.XtraGrid.Columns.GridColumn colHolder;
        protected DevExpress.XtraGrid.Columns.GridColumn colIban;
        protected DevExpress.XtraEditors.SimpleButton btnVerify;
        protected DevExpress.XtraEditors.LabelControl lblCurrency;
        protected DevExpress.XtraEditors.LabelControl lblStatus;
    }
}
