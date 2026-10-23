namespace SampleLegacy.Forms
{
    partial class BeneficiarySearchForm
    {
        private System.ComponentModel.IContainer components = null;

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            DevExpress.XtraEditors.EditorButtonImageOptions editorButtonImageOptions1 = new DevExpress.XtraEditors.EditorButtonImageOptions();
            this.imgIcons = new System.Windows.Forms.ImageList(this.components);
            this.lstResults = new System.Windows.Forms.ListView();
            this.txtName = new DevExpress.XtraEditors.ButtonEdit();
            this.SuspendLayout();
            // 
            // imgIcons
            // 
            this.imgIcons.TransparentColor = System.Drawing.Color.Transparent;
            this.imgIcons.Images.SetKeyName(0, "person.png");
            this.imgIcons.Images.SetKeyName(1, "company.png");
            // 
            // lstResults
            // 
            this.lstResults.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this._lstResults_ColumnHeader_1,
            this._lstResults_ColumnHeader_2});
            this.lstResults.FullRowSelect = true;
            this.lstResults.Name = "lstResults";
            this.lstResults.SelectedIndexChanged += new System.EventHandler(this.lstResults_SelectedIndexChanged);
            // 
            // txtName
            // 
            editorButtonImageOptions1.SvgImage = "search.svg";
            this.txtName.Name = "txtName";
            this.txtName.Properties.Buttons.AddRange(new DevExpress.XtraEditors.EditorButton[] {
            new DevExpress.XtraEditors.EditorButton("Glyph", "", -1, true, true, false, editorButtonImageOptions1)});
            this.txtName.Properties.ButtonClick += new System.EventHandler(this.txtName_ButtonClick);
            // 
            // inherited members
            // 
            this.btnOK.Text = "Select";
            this.btnOK.Enabled = false;
            this.btnCancel.Text = "Close";
            this.pnlMain.Controls.Add(this.lstResults);
            this.pnlMain.Controls.Add(this.txtName);
            this.Appearance.BackColor = System.Drawing.Color.White;
            this.IconOptions.ShowIcon = false;
            // 
            // BeneficiarySearchForm
            // 
            this.Controls.Add(this.pnlMain);
            this.Name = "BeneficiarySearchForm";
            this.Text = "Find Beneficiary";
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.ImageList imgIcons;
        private System.Windows.Forms.ListView lstResults;
        private System.Windows.Forms.ColumnHeader _lstResults_ColumnHeader_1;
        private System.Windows.Forms.ColumnHeader _lstResults_ColumnHeader_2;
        private DevExpress.XtraEditors.ButtonEdit txtName;
    }
}
