using System.Collections.Generic;

namespace SampleLegacy.Services
{
    public interface IClientService
    {
        List<ClientDto> SearchClients(ClientSearchCriteria criteria);
        List<ClientDto> SearchLoyaltyClients(ClientSearchCriteria criteria, bool membersOnly);
        ClientDto GetClient(int clientId);
    }
}
