using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DevExpress.Utils
{
    public enum DefaultBoolean { Default, True, False }
}

namespace DevExpress.XtraEditors
{
    public class FormIconOptions { public object Icon { get; set; } public bool ShowIcon { get; set; } }
    public class AppearanceObject { public Color BackColor { get; set; } public Color ForeColor { get; set; } }
    public class XtraForm : Form { public FormIconOptions IconOptions { get; } = new FormIconOptions(); public AppearanceObject Appearance { get; } = new AppearanceObject(); }
    public class EditorButtonImageOptions { public object Image { get; set; } public string SvgImage { get; set; } }
    public class EditorButton { public EditorButton() { } public EditorButton(string kind, string caption, int width, bool enabled, bool visible, bool isLeft, EditorButtonImageOptions imageOptions) { } }
    public class EditorButtonCollection : List<EditorButton> { public void AddRange(EditorButton[] b) => base.AddRange(b); }
    public class XtraUserControl : UserControl { }
    public class SimpleButton : Control { }
    public class LabelControl : Control { }
    public class PanelControl : Control { }
    public class GroupControl : Control { public string Caption { get; set; } }

    namespace Mask { public class MaskProperties { public string EditMask { get; set; } public bool UseMaskAsDisplayFormat { get; set; } } }

    namespace Repository
    {
        public class RepositoryItem : Component
        {
            public bool ReadOnly { get; set; }
            public string NullText { get; set; }
            public Mask.MaskProperties Mask { get; } = new Mask.MaskProperties();
            public string DisplayFormat { get; set; }
            public bool AllowFocused { get; set; }
            public event EventHandler ButtonClick;
        }
        public class RepositoryItemTextEdit : RepositoryItem { }
        public class RepositoryItemButtonEdit : RepositoryItem { public EditorButtonCollection Buttons { get; } = new EditorButtonCollection(); }
        public class RepositoryItemLookUpEdit : RepositoryItem
        {
            public object DataSource { get; set; }
            public string DisplayMember { get; set; }
            public string ValueMember { get; set; }
            public LookUpColumnInfoCollection Columns { get; } = new LookUpColumnInfoCollection();
        }
        public class RepositoryItemCheckEdit : RepositoryItem { public string Caption { get; set; } }
        public class RepositoryItemDateEdit : RepositoryItem { }
        public class RepositoryItemComboBox : RepositoryItem { public List<object> Items { get; } = new List<object>(); }
    }

    namespace Controls
    {
        public class LookUpColumnInfo { public LookUpColumnInfo(string fieldName, string caption) { } }
        public class LookUpColumnInfoCollection : List<LookUpColumnInfo> { public void AddRange(LookUpColumnInfo[] items) => base.AddRange(items); }
    }
    public class LookUpColumnInfoCollection : List<Controls.LookUpColumnInfo> { public void AddRange(Controls.LookUpColumnInfo[] items) => base.AddRange(items); }

    public class BaseEdit : Control
    {
        public object EditValue { get; set; }
        public event EventHandler EditValueChanged;
        public void SelectAll() { }
        public string ErrorText { get; set; }
    }
    public class TextEdit : BaseEdit { public Repository.RepositoryItemTextEdit Properties { get; } = new Repository.RepositoryItemTextEdit(); }
    public class ButtonEdit : BaseEdit { public Repository.RepositoryItemButtonEdit Properties { get; } = new Repository.RepositoryItemButtonEdit(); }
    public class LookUpEdit : BaseEdit { public Repository.RepositoryItemLookUpEdit Properties { get; } = new Repository.RepositoryItemLookUpEdit(); }
    public class CheckEdit : BaseEdit { public Repository.RepositoryItemCheckEdit Properties { get; } = new Repository.RepositoryItemCheckEdit(); public bool Checked { get; set; } public event EventHandler CheckedChanged; }
    public class DateEdit : BaseEdit { public Repository.RepositoryItemDateEdit Properties { get; } = new Repository.RepositoryItemDateEdit(); public DateTime DateTime { get; set; } }
    public class ComboBoxEdit : BaseEdit { public Repository.RepositoryItemComboBox Properties { get; } = new Repository.RepositoryItemComboBox(); public event EventHandler SelectedIndexChanged; }
    public class SpinEdit : BaseEdit { }

    namespace DXErrorProvider
    {
        public enum ErrorType { None, Warning, Critical }
        public enum ConditionOperator { Equals, NotEquals, Greater, Less, IsNotBlank, Between }
        public class ValidationRule { public string ErrorText { get; set; } public ErrorType ErrorType { get; set; } }
        public class ConditionValidationRule : ValidationRule
        {
            public ConditionValidationRule() { }
            public ConditionValidationRule(ConditionOperator op) { }
            public ConditionOperator ConditionOperator { get; set; }
            public object Value1 { get; set; }
            public object Value2 { get; set; }
        }
        public class DXValidationProvider : Component
        {
            public DXValidationProvider() { }
            public DXValidationProvider(IContainer container) { }
            public void SetValidationRule(Control control, ValidationRule rule) { }
            public bool Validate() => true;
            public bool Validate(Control control) => true;
            public event EventHandler ValidationFailed;
        }
        public class DXErrorProvider : Component
        {
            public DXErrorProvider() { }
            public DXErrorProvider(IContainer container) { }
            public void SetError(Control control, string errorText) { }
            public void SetError(Control control, string errorText, ErrorType errorType) { }
            public void ClearErrors() { }
        }
    }
}

namespace DevExpress.XtraGrid
{
    public class GridControl : Control
    {
        public object DataSource { get; set; }
        public Views.Base.BaseView MainView { get; set; }
        public ViewCollection ViewCollection { get; } = new ViewCollection();
        public Repository.RepositoryItemCollection RepositoryItems { get; } = new Repository.RepositoryItemCollection();
        public void BeginInit() { }
        public void EndInit() { }
    }
    public class ViewCollection : List<Views.Base.BaseView> { public void AddRange(Views.Base.BaseView[] views) => base.AddRange(views); }

    namespace Repository
    {
        public class RepositoryItemCollection : List<XtraEditors.Repository.RepositoryItem> { public void AddRange(XtraEditors.Repository.RepositoryItem[] items) => base.AddRange(items); }
    }

    namespace Columns
    {
        public class GridColumn : Component
        {
            public string Caption { get; set; }
            public string FieldName { get; set; }
            public string Name { get; set; }
            public bool Visible { get; set; }
            public int VisibleIndex { get; set; }
            public int Width { get; set; }
            public XtraEditors.Repository.RepositoryItem ColumnEdit { get; set; }
            public OptionsColumn OptionsColumn { get; } = new OptionsColumn();
        }
        public class OptionsColumn { public bool AllowEdit { get; set; } public bool ReadOnly { get; set; } }
        public class GridColumnCollection : List<GridColumn> { public void AddRange(GridColumn[] columns) => base.AddRange(columns); }
    }

    namespace Views
    {
        namespace Base
        {
            public class BaseView : Component
            {
                public GridControl GridControl { get; set; }
                public string Name { get; set; }
                public event EventHandler DataSourceChanged;
            }
            public class ColumnView : BaseView
            {
                public Columns.GridColumnCollection Columns { get; } = new Columns.GridColumnCollection();
                public int FocusedRowHandle { get; set; }
                public object GetFocusedRow() => null;
                public object GetRow(int handle) => null;
                public event EventHandler FocusedRowChanged;
                public event EventHandler RowUpdated;
                public event EventHandler DoubleClick;
                public event EventHandler ValidateRow;
                public event EventHandler CellValueChanged;
                public void RefreshData() { }
            }
        }
        namespace Grid
        {
            public class GridView : Base.ColumnView
            {
                public GridViewOptions OptionsBehavior { get; } = new GridViewOptions();
                public GridViewOptions OptionsView { get; } = new GridViewOptions();
                public GridViewOptions OptionsSelection { get; } = new GridViewOptions();
                public void SelectRow(int handle) { }
                public void BestFitColumns() { }
            }
            public class GridViewOptions { public bool Editable { get; set; } public bool ShowGroupPanel { get; set; } public bool MultiSelect { get; set; } public bool ReadOnly { get; set; } public bool AutoExpandAllGroups { get; set; } }
        }
    }
}

namespace DevExpress.XtraLayout
{
    public enum TextLocation { Left, Top }
    public class BaseLayoutItem : Component
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public bool TextVisible { get; set; }
        public Point Location { get; set; }
        public Size Size { get; set; }
        public string CustomizationFormText { get; set; }
        public DevExpress.Utils.DefaultBoolean AllowHide { get; set; }
    }
    public class LayoutControlItem : BaseLayoutItem { public Control Control { get; set; } public Size TextSize { get; set; } public TextLocation TextLocation { get; set; } }
    public class EmptySpaceItem : BaseLayoutItem { }
    public class LayoutItemCollection : List<BaseLayoutItem> { public void AddRange(BaseLayoutItem[] items) => base.AddRange(items); }
    public class LayoutControlGroup : BaseLayoutItem { public LayoutItemCollection Items { get; } = new LayoutItemCollection(); public bool GroupBordersVisible { get; set; } }
    public class LayoutControl : Control { public LayoutControlGroup Root { get; set; } public void BeginInit() { } public void EndInit() { } }
}
