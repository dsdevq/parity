using System;
using System.Collections.Generic;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using SampleLegacy.Helpers;
using SampleLegacy.Services;

namespace SampleLegacy.Forms
{
    public partial class DynamicRidersForm : XtraForm
    {
        private readonly PolicyDto _policy;
        private readonly List<CheckEdit> _riderChecks = new List<CheckEdit>();

        public DynamicRidersForm(PolicyDto policy)
        {
            InitializeComponent();
            _policy = policy;
            this.Load += DynamicRidersForm_Load;
        }

        private void DynamicRidersForm_Load(object sender, EventArgs e)
        {
            var riders = ServiceLocator.Policy.GetAvailableRiders(_policy.ProductId);
            int top = 8;
            foreach (var rider in riders)
            {
                var chk = new CheckEdit();
                chk.Name = "chkRider" + rider.RiderId;
                chk.Properties.Caption = rider.Name + " (+" + rider.Loading + "%)";
                chk.Tag = rider;
                chk.Location = new System.Drawing.Point(8, top);
                chk.CheckedChanged += Rider_CheckedChanged;
                pnlRiders.Controls.Add(chk);
                _riderChecks.Add(chk);
                top += 24;
            }
        }

        private void Rider_CheckedChanged(object sender, EventArgs e)
        {
            btnApply.Enabled = _riderChecks.Exists(c => c.Checked);
        }

        private void btnApply_Click(object sender, EventArgs e)
        {
            _policy.Riders = new List<RiderDto>();
            foreach (var chk in _riderChecks)
                if (chk.Checked) _policy.Riders.Add((RiderDto)chk.Tag);
            ServiceLocator.Policy.SavePolicyStep(_policy, 4);
            Close();
        }
    }
}
