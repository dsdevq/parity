using SampleLegacy.Forms;

namespace SampleLegacy.Tenants.Thistle
{
    /// <summary>Tenant variant: Thistle pays in one currency, so the currency is not shown.</summary>
    public class ThistlePayeeLookupPanel : PayeeLookupPanel
    {
        public ThistlePayeeLookupPanel()
        {
            lblCurrency.Visible = false;
        }
    }
}
