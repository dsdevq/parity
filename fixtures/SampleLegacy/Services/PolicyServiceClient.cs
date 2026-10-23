using System.Collections.Generic;

namespace SampleLegacy.Services
{
    // Stand-in for the generated WCF/old-API proxy. At work this is the type behind the gateway.
    public class PolicyServiceClient : IPolicyService
    {
        public PremiumLimitsDto GetPremiumLimits(int productId) { return new PremiumLimitsDto(); }
        public List<ProductDto> GetProducts() { return new List<ProductDto>(); }
        public void SavePolicyStep(PolicyDto policy, int step) { }
        public List<RiderDto> GetAvailableRiders(int productId) { return new List<RiderDto>(); }
        public void AddBeneficiary(int policyId, int clientId) { }
    }

    public class ClientServiceClient : IClientService
    {
        public List<ClientDto> SearchClients(ClientSearchCriteria criteria) { return new List<ClientDto>(); }
        public List<ClientDto> SearchLoyaltyClients(ClientSearchCriteria criteria, bool membersOnly) { return new List<ClientDto>(); }
        public ClientDto GetClient(int clientId) { return new ClientDto(); }
    }

    public static class ServiceLocator
    {
        public static IPolicyService Policy = new PolicyServiceClient();
        public static IClientService Client = new ClientServiceClient();
    }
}
