using System.Collections.Generic;

namespace SampleLegacy.Services
{
    public interface IClientService
    {
        List<ClientDto> SearchClients(ClientSearchCriteria criteria);
        ClientDto GetClient(int clientId);
    }
}
