using SampleLegacy.Tenants.Larkspur;
using SampleLegacy.Tenants.Thistle;

namespace SampleLegacy.Forms
{
    /// <summary>Which payee panel a tenant gets.</summary>
    public static class PayeeLookupPanelFactory
    {
        public static PayeeLookupPanel For(string tenantCode)
        {
            switch (tenantCode)
            {
                case "LRK":
                    return new LarkspurPayeeLookupPanel();
                case "THS":
                    return new ThistlePayeeLookupPanel();
                default:
                    return new PayeeLookupPanel();
            }
        }
    }
}
