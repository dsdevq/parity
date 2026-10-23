using System.Collections.Generic;

namespace SampleLegacy.Services
{
    public interface IPolicyService
    {
        PremiumLimitsDto GetPremiumLimits(int productId);
        List<ProductDto> GetProducts();
        void SavePolicyStep(PolicyDto policy, int step);
        List<RiderDto> GetAvailableRiders(int productId);
    }
}
