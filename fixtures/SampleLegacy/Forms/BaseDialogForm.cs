using System;
using System.Windows.Forms;
using DevExpress.XtraEditors;

namespace SampleLegacy.Forms
{
    // Custom base form: OK/Cancel and the main panel live here, derived forms use them in their designer.
    public class BaseDialogForm : XtraForm
    {
        protected SimpleButton btnOK = new SimpleButton();
        protected SimpleButton btnCancel = new SimpleButton();
        protected PanelControl pnlMain = new PanelControl();

        public BaseDialogForm()
        {
            btnOK.Click += (s, e) => OnAccept();
        }

        protected virtual void OnAccept() { }
    }
}
