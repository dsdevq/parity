using System.Windows.Forms;

namespace SampleLegacy.Helpers
{
    public static class UiHelper
    {
        public static void Warn(string message)
        {
            MessageBox.Show(message, "AcmeAdmin", MessageBoxButtons.OK);
        }

        public static bool Confirm(string message)
        {
            return MessageBox.Show(message, "AcmeAdmin", MessageBoxButtons.YesNo) == DialogResult.OK;
        }
    }
}
