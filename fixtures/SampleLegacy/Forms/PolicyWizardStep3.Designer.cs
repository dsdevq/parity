namespace SampleLegacy.Forms
{
    partial class PolicyWizardStep3
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(PolicyWizardStep3));
            this.layoutControl1 = new DevExpress.XtraLayout.LayoutControl();
            this.txtPremium = new DevExpress.XtraEditors.TextEdit();
            this.cboProduct = new DevExpress.XtraEditors.LookUpEdit();
            this.chkSinglePremium = new DevExpress.XtraEditors.CheckEdit();
            this.cboFrequency = new DevExpress.XtraEditors.ComboBoxEdit();
            this.txtInstallments = new DevExpress.XtraEditors.SpinEdit();
            this.dtStart = new DevExpress.XtraEditors.DateEdit();
            this.grdRiders = new DevExpress.XtraGrid.GridControl();
            this.gvRiders = new DevExpress.XtraGrid.Views.Grid.GridView();
            this.colRiderName = new DevExpress.XtraGrid.Columns.GridColumn();
            this.colLoading = new DevExpress.XtraGrid.Columns.GridColumn();
            this.btnNext = new DevExpress.XtraEditors.SimpleButton();
            this.btnBack = new DevExpress.XtraEditors.SimpleButton();
            this.btnLegacyPrint = new DevExpress.XtraEditors.SimpleButton();
            this.lblTotal = new DevExpress.XtraEditors.LabelControl();
            this.layoutControlGroup1 = new DevExpress.XtraLayout.LayoutControlGroup();
            this.layoutControlItem1 = new DevExpress.XtraLayout.LayoutControlItem();
            this.layoutControlItem2 = new DevExpress.XtraLayout.LayoutControlItem();
            this.layoutControlItem3 = new DevExpress.XtraLayout.LayoutControlItem();
            this.layoutControlItem4 = new DevExpress.XtraLayout.LayoutControlItem();
            this.layoutControlItem5 = new DevExpress.XtraLayout.LayoutControlItem();
            this.layoutControlItem6 = new DevExpress.XtraLayout.LayoutControlItem();
            this.layoutControlItem7 = new DevExpress.XtraLayout.LayoutControlItem();
            this.emptySpaceItem1 = new DevExpress.XtraLayout.EmptySpaceItem();
            this.panelButtons = new DevExpress.XtraEditors.PanelControl();
            this.policyBindingSource = new System.Windows.Forms.BindingSource(this.components);
            this.validationProvider = new DevExpress.XtraEditors.DXErrorProvider.DXValidationProvider(this.components);
            this.errorProvider = new DevExpress.XtraEditors.DXErrorProvider.DXErrorProvider(this.components);
            this.premiumRule = new DevExpress.XtraEditors.DXErrorProvider.ConditionValidationRule();
            ((System.ComponentModel.ISupportInitialize)(this.layoutControl1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.grdRiders)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.gvRiders)).BeginInit();
            this.layoutControl1.SuspendLayout();
            this.panelButtons.SuspendLayout();
            this.SuspendLayout();
            // 
            // layoutControl1
            // 
            this.layoutControl1.Controls.Add(this.txtPremium);
            this.layoutControl1.Controls.Add(this.cboProduct);
            this.layoutControl1.Controls.Add(this.chkSinglePremium);
            this.layoutControl1.Controls.Add(this.cboFrequency);
            this.layoutControl1.Controls.Add(this.txtInstallments);
            this.layoutControl1.Controls.Add(this.dtStart);
            this.layoutControl1.Controls.Add(this.grdRiders);
            this.layoutControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutControl1.Location = new System.Drawing.Point(0, 0);
            this.layoutControl1.Name = "layoutControl1";
            this.layoutControl1.Root = this.layoutControlGroup1;
            this.layoutControl1.Size = new System.Drawing.Size(640, 420);
            this.layoutControl1.TabIndex = 0;
            this.layoutControl1.Text = "layoutControl1";
            // 
            // txtPremium
            // 
            this.txtPremium.DataBindings.Add(new System.Windows.Forms.Binding("EditValue", this.policyBindingSource, "Premium", true));
            this.txtPremium.Location = new System.Drawing.Point(120, 36);
            this.txtPremium.Name = "txtPremium";
            this.txtPremium.Properties.Mask.EditMask = "c2";
            this.txtPremium.Properties.Mask.UseMaskAsDisplayFormat = true;
            this.txtPremium.Size = new System.Drawing.Size(200, 20);
            this.txtPremium.TabIndex = 2;
            this.txtPremium.Validating += new System.ComponentModel.CancelEventHandler(this.txtPremium_Validating);
            this.txtPremium.Enter += new System.EventHandler(this.txtPremium_Enter);
            this.txtPremium.EditValueChanged += new System.EventHandler(this.txtPremium_EditValueChanged);
            // 
            // cboProduct
            // 
            this.cboProduct.DataBindings.Add(new System.Windows.Forms.Binding("EditValue", this.policyBindingSource, "ProductId", true));
            this.cboProduct.Location = new System.Drawing.Point(120, 12);
            this.cboProduct.Name = "cboProduct";
            this.cboProduct.Properties.Columns.AddRange(new DevExpress.XtraEditors.Controls.LookUpColumnInfo[] {
            new DevExpress.XtraEditors.Controls.LookUpColumnInfo("Name", "Product"),
            new DevExpress.XtraEditors.Controls.LookUpColumnInfo("MaxPremium", "Max")});
            this.cboProduct.Properties.DisplayMember = "Name";
            this.cboProduct.Properties.NullText = resources.GetString("cboProduct.Properties.NullText");
            this.cboProduct.Properties.ValueMember = "ProductId";
            this.cboProduct.Size = new System.Drawing.Size(200, 20);
            this.cboProduct.TabIndex = 1;
            this.cboProduct.EditValueChanged += new System.EventHandler(this.cboProduct_EditValueChanged);
            // 
            // chkSinglePremium
            // 
            this.chkSinglePremium.DataBindings.Add(new System.Windows.Forms.Binding("EditValue", this.policyBindingSource, "SinglePremium", true));
            this.chkSinglePremium.Location = new System.Drawing.Point(120, 60);
            this.chkSinglePremium.Name = "chkSinglePremium";
            this.chkSinglePremium.Properties.Caption = resources.GetString("chkSinglePremium.Properties.Caption");
            this.chkSinglePremium.Size = new System.Drawing.Size(200, 19);
            this.chkSinglePremium.TabIndex = 3;
            this.chkSinglePremium.CheckedChanged += new System.EventHandler(this.chkSinglePremium_CheckedChanged);
            // 
            // cboFrequency
            // 
            this.cboFrequency.DataBindings.Add(new System.Windows.Forms.Binding("EditValue", this.policyBindingSource, "Frequency", true));
            this.cboFrequency.Location = new System.Drawing.Point(120, 83);
            this.cboFrequency.Name = "cboFrequency";
            this.cboFrequency.Properties.Items.AddRange(new object[] {
            "Annual",
            "Quarterly",
            "Monthly"});
            this.cboFrequency.Size = new System.Drawing.Size(200, 20);
            this.cboFrequency.TabIndex = 4;
            this.cboFrequency.SelectedIndexChanged += new System.EventHandler(this.cboFrequency_SelectedIndexChanged);
            // 
            // txtInstallments
            // 
            this.txtInstallments.DataBindings.Add(new System.Windows.Forms.Binding("EditValue", this.policyBindingSource, "Installments", true));
            this.txtInstallments.Location = new System.Drawing.Point(120, 107);
            this.txtInstallments.Name = "txtInstallments";
            this.txtInstallments.Size = new System.Drawing.Size(200, 20);
            this.txtInstallments.TabIndex = 5;
            // 
            // dtStart
            // 
            this.dtStart.DataBindings.Add(new System.Windows.Forms.Binding("EditValue", this.policyBindingSource, "StartDate", true));
            this.dtStart.Location = new System.Drawing.Point(120, 131);
            this.dtStart.Name = "dtStart";
            this.dtStart.Size = new System.Drawing.Size(200, 20);
            this.dtStart.TabIndex = 6;
            // 
            // grdRiders
            // 
            this.grdRiders.DataSource = this.policyBindingSource;
            this.grdRiders.Location = new System.Drawing.Point(12, 155);
            this.grdRiders.MainView = this.gvRiders;
            this.grdRiders.Name = "grdRiders";
            this.grdRiders.Size = new System.Drawing.Size(616, 200);
            this.grdRiders.TabIndex = 7;
            this.grdRiders.ViewCollection.AddRange(new DevExpress.XtraGrid.Views.Base.BaseView[] {
            this.gvRiders});
            // 
            // gvRiders
            // 
            this.gvRiders.Columns.AddRange(new DevExpress.XtraGrid.Columns.GridColumn[] {
            this.colRiderName,
            this.colLoading});
            this.gvRiders.GridControl = this.grdRiders;
            this.gvRiders.Name = "gvRiders";
            this.gvRiders.OptionsView.ShowGroupPanel = false;
            this.gvRiders.CellValueChanged += new System.EventHandler(this.gvRiders_CellValueChanged);
            // 
            // colRiderName
            // 
            this.colRiderName.Caption = "Rider";
            this.colRiderName.FieldName = "Name";
            this.colRiderName.Name = "colRiderName";
            this.colRiderName.OptionsColumn.AllowEdit = false;
            this.colRiderName.Visible = true;
            this.colRiderName.VisibleIndex = 0;
            // 
            // colLoading
            // 
            this.colLoading.Caption = "Loading %";
            this.colLoading.FieldName = "Loading";
            this.colLoading.Name = "colLoading";
            this.colLoading.Visible = true;
            this.colLoading.VisibleIndex = 1;
            // 
            // panelButtons
            // 
            this.panelButtons.Controls.Add(this.btnNext);
            this.panelButtons.Controls.Add(this.btnBack);
            this.panelButtons.Controls.Add(this.btnLegacyPrint);
            this.panelButtons.Controls.Add(this.lblTotal);
            this.panelButtons.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.panelButtons.Location = new System.Drawing.Point(0, 420);
            this.panelButtons.Name = "panelButtons";
            this.panelButtons.Size = new System.Drawing.Size(640, 40);
            this.panelButtons.TabIndex = 1;
            // 
            // btnNext
            // 
            this.btnNext.Location = new System.Drawing.Point(540, 8);
            this.btnNext.Name = "btnNext";
            this.btnNext.Size = new System.Drawing.Size(88, 24);
            this.btnNext.TabIndex = 0;
            this.btnNext.Text = "Next >";
            this.btnNext.Click += new System.EventHandler(this.btnNext_Click);
            // 
            // btnBack
            // 
            this.btnBack.Location = new System.Drawing.Point(446, 8);
            this.btnBack.Name = "btnBack";
            this.btnBack.Size = new System.Drawing.Size(88, 24);
            this.btnBack.TabIndex = 1;
            this.btnBack.Text = "< Back";
            this.btnBack.Click += new System.EventHandler(this.btnBack_Click);
            // 
            // btnLegacyPrint
            // 
            this.btnLegacyPrint.Location = new System.Drawing.Point(12, 8);
            this.btnLegacyPrint.Name = "btnLegacyPrint";
            this.btnLegacyPrint.Size = new System.Drawing.Size(88, 24);
            this.btnLegacyPrint.TabIndex = 2;
            this.btnLegacyPrint.Text = "Print";
            this.btnLegacyPrint.Visible = false;
            this.btnLegacyPrint.Click += new System.EventHandler(this.btnLegacyPrint_Click);
            // 
            // lblTotal
            // 
            this.lblTotal.Location = new System.Drawing.Point(200, 12);
            this.lblTotal.Name = "lblTotal";
            this.lblTotal.Size = new System.Drawing.Size(120, 13);
            this.lblTotal.TabIndex = 3;
            this.lblTotal.Text = "Total: 0.00";
            // 
            // layoutControlGroup1
            // 
            this.layoutControlGroup1.GroupBordersVisible = false;
            this.layoutControlGroup1.Items.AddRange(new DevExpress.XtraLayout.BaseLayoutItem[] {
            this.layoutControlItem1,
            this.layoutControlItem2,
            this.layoutControlItem3,
            this.layoutControlItem4,
            this.layoutControlItem5,
            this.layoutControlItem6,
            this.layoutControlItem7,
            this.emptySpaceItem1});
            this.layoutControlGroup1.Name = "Root";
            this.layoutControlGroup1.Size = new System.Drawing.Size(640, 420);
            this.layoutControlGroup1.TextVisible = false;
            // 
            // layoutControlItem1
            // 
            this.layoutControlItem1.Control = this.cboProduct;
            this.layoutControlItem1.Name = "layoutControlItem1";
            this.layoutControlItem1.Text = "Product";
            this.layoutControlItem1.TextSize = new System.Drawing.Size(100, 13);
            // 
            // layoutControlItem2
            // 
            this.layoutControlItem2.Control = this.txtPremium;
            this.layoutControlItem2.Name = "layoutControlItem2";
            this.layoutControlItem2.Text = resources.GetString("layoutControlItem2.Text");
            this.layoutControlItem2.TextSize = new System.Drawing.Size(100, 13);
            // 
            // layoutControlItem3
            // 
            this.layoutControlItem3.Control = this.chkSinglePremium;
            this.layoutControlItem3.Name = "layoutControlItem3";
            this.layoutControlItem3.TextVisible = false;
            // 
            // layoutControlItem4
            // 
            this.layoutControlItem4.Control = this.cboFrequency;
            this.layoutControlItem4.Name = "layoutControlItem4";
            this.layoutControlItem4.Text = "Frequency";
            // 
            // layoutControlItem5
            // 
            this.layoutControlItem5.Control = this.txtInstallments;
            this.layoutControlItem5.Name = "layoutControlItem5";
            this.layoutControlItem5.Text = "Installments";
            // 
            // layoutControlItem6
            // 
            this.layoutControlItem6.Control = this.dtStart;
            this.layoutControlItem6.Name = "layoutControlItem6";
            this.layoutControlItem6.Text = "Start date";
            // 
            // layoutControlItem7
            // 
            this.layoutControlItem7.Control = this.grdRiders;
            this.layoutControlItem7.Name = "layoutControlItem7";
            this.layoutControlItem7.Text = "Riders";
            this.layoutControlItem7.TextLocation = DevExpress.XtraLayout.TextLocation.Top;
            // 
            // emptySpaceItem1
            // 
            this.emptySpaceItem1.AllowHide = DevExpress.Utils.DefaultBoolean.False;
            this.emptySpaceItem1.Name = "emptySpaceItem1";
            // 
            // policyBindingSource
            // 
            this.policyBindingSource.DataSource = typeof(SampleLegacy.Services.PolicyDto);
            // 
            // premiumRule
            // 
            this.premiumRule.ConditionOperator = DevExpress.XtraEditors.DXErrorProvider.ConditionOperator.IsNotBlank;
            this.premiumRule.ErrorText = "Premium is required";
            this.validationProvider.SetValidationRule(this.txtPremium, this.premiumRule);
            // 
            // PolicyWizardStep3
            // 
            this.Controls.Add(this.layoutControl1);
            this.Controls.Add(this.panelButtons);
            this.Name = "PolicyWizardStep3";
            this.Text = "New Business - Step 3: Premium";
            this.Load += new System.EventHandler(this.PolicyWizardStep3_Load);
            ((System.ComponentModel.ISupportInitialize)(this.layoutControl1)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.grdRiders)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.gvRiders)).EndInit();
            this.layoutControl1.ResumeLayout(false);
            this.panelButtons.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private DevExpress.XtraLayout.LayoutControl layoutControl1;
        private DevExpress.XtraEditors.TextEdit txtPremium;
        private DevExpress.XtraEditors.LookUpEdit cboProduct;
        private DevExpress.XtraEditors.CheckEdit chkSinglePremium;
        private DevExpress.XtraEditors.ComboBoxEdit cboFrequency;
        private DevExpress.XtraEditors.SpinEdit txtInstallments;
        private DevExpress.XtraEditors.DateEdit dtStart;
        private DevExpress.XtraGrid.GridControl grdRiders;
        private DevExpress.XtraGrid.Views.Grid.GridView gvRiders;
        private DevExpress.XtraGrid.Columns.GridColumn colRiderName;
        private DevExpress.XtraGrid.Columns.GridColumn colLoading;
        private DevExpress.XtraEditors.SimpleButton btnNext;
        private DevExpress.XtraEditors.SimpleButton btnBack;
        private DevExpress.XtraEditors.SimpleButton btnLegacyPrint;
        private DevExpress.XtraEditors.LabelControl lblTotal;
        private DevExpress.XtraLayout.LayoutControlGroup layoutControlGroup1;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem1;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem2;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem3;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem4;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem5;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem6;
        private DevExpress.XtraLayout.LayoutControlItem layoutControlItem7;
        private DevExpress.XtraLayout.EmptySpaceItem emptySpaceItem1;
        private DevExpress.XtraEditors.PanelControl panelButtons;
        private System.Windows.Forms.BindingSource policyBindingSource;
        private DevExpress.XtraEditors.DXErrorProvider.DXValidationProvider validationProvider;
        private DevExpress.XtraEditors.DXErrorProvider.DXErrorProvider errorProvider;
        private DevExpress.XtraEditors.DXErrorProvider.ConditionValidationRule premiumRule;
    }
}
