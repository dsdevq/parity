using System;
using System.Collections.Generic;
using System.Windows.Forms;
using SampleLegacy.Services;

namespace SampleLegacy.Forms
{
    public class PolicyWizardController
    {
        private readonly PolicyDto _policy;
        private int _step = 1;
        private const int LastStep = 5;

        public PolicyWizardController(PolicyDto policy) { _policy = policy; }

        public bool CanGoBack { get { return _step > 1; } }

        public void Next()
        {
            // step 4 (riders) is skipped for single-premium policies
            if (_step == 3 && _policy.SinglePremium) _step = 5;
            else if (_step < LastStep) _step++;
            Show(_step);
        }

        public void Back()
        {
            if (_step == 5 && _policy.SinglePremium) _step = 3;
            else if (_step > 1) _step--;
            Show(_step);
        }

        private void Show(int step)
        {
            Form form;
            switch (step)
            {
                case 3: form = new PolicyWizardStep3(this, _policy); break;
                case 4: form = new DynamicRidersForm(_policy); break;
                default: form = new Form(); break;
            }
            form.ShowDialog();
        }
    }
}
