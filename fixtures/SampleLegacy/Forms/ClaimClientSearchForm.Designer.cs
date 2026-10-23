namespace SampleLegacy.Forms
{
    partial class ClaimClientSearchForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.txtSurname = new DevExpress.XtraEditors.TextEdit();
            this.txtPostcode = new DevExpress.XtraEditors.TextEdit();
            this.dtDob = new DevExpress.XtraEditors.DateEdit();
            this.btnSearch = new DevExpress.XtraEditors.SimpleButton();
            this.btnSelect = new DevExpress.XtraEditors.SimpleButton();
            this.grdResults = new DevExpress.XtraGrid.GridControl();
            this.gvResults = new DevExpress.XtraGrid.Views.Grid.GridView();
            this.colSurname = new DevExpress.XtraGrid.Columns.GridColumn();
            this.colForename = new DevExpress.XtraGrid.Columns.GridColumn();
            this.colDob = new DevExpress.XtraGrid.Columns.GridColumn();
            this.SuspendLayout();
            this.txtSurname.Name = "txtSurname";
            this.txtSurname.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtSurname_KeyDown);
            this.txtPostcode.Name = "txtPostcode";
            this.dtDob.Name = "dtDob";
            this.btnSearch.Name = "btnSearch";
            this.btnSearch.Text = "Find";
            this.btnSearch.Click += new System.EventHandler(this.btnSearch_Click);
            this.btnSelect.Name = "btnSelect";
            this.btnSelect.Text = "Select";
            this.btnSelect.Enabled = false;
            this.btnSelect.Click += new System.EventHandler(this.btnSelect_Click);
            this.grdResults.MainView = this.gvResults;
            this.grdResults.Name = "grdResults";
            this.gvResults.Columns.AddRange(new DevExpress.XtraGrid.Columns.GridColumn[] { this.colSurname, this.colForename, this.colDob });
            this.gvResults.GridControl = this.grdResults;
            this.gvResults.Name = "gvResults";
            this.gvResults.FocusedRowChanged += new System.EventHandler(this.gvResults_FocusedRowChanged);
            this.gvResults.DoubleClick += new System.EventHandler(this.gvResults_DoubleClick);
            this.colSurname.FieldName = "Surname";
            this.colSurname.Caption = "Surname";
            this.colSurname.Visible = true;
            this.colForename.FieldName = "Forename";
            this.colForename.Caption = "Forename";
            this.colForename.Visible = true;
            this.colDob.FieldName = "DateOfBirth";
            this.colDob.Caption = "Date of birth";
            this.colDob.Visible = true;
            this.Controls.Add(this.txtSurname);
            this.Controls.Add(this.txtPostcode);
            this.Controls.Add(this.dtDob);
            this.Controls.Add(this.btnSearch);
            this.Controls.Add(this.btnSelect);
            this.Controls.Add(this.grdResults);
            this.Name = "ClaimClientSearchForm";
            this.Text = "Find Claimant";
            this.ResumeLayout(false);
        }

        private DevExpress.XtraEditors.TextEdit txtSurname;
        private DevExpress.XtraEditors.TextEdit txtPostcode;
        private DevExpress.XtraEditors.DateEdit dtDob;
        private DevExpress.XtraEditors.SimpleButton btnSearch;
        private DevExpress.XtraEditors.SimpleButton btnSelect;
        private DevExpress.XtraGrid.GridControl grdResults;
        private DevExpress.XtraGrid.Views.Grid.GridView gvResults;
        private DevExpress.XtraGrid.Columns.GridColumn colSurname;
        private DevExpress.XtraGrid.Columns.GridColumn colForename;
        private DevExpress.XtraGrid.Columns.GridColumn colDob;
    }
}
