using System;
using System.Collections.Generic;
using SampleLegacy.Tenants.Larkspur;
using SampleLegacy.Tenants.Thistle;

namespace SampleLegacy.Forms
{
    /// <summary>Which client search a tenant gets: by tenant code, and by sales region for the call centre.</summary>
    public static class ClientSearchFormFactory
    {
        public static ClientSearchForm Create(string tenantCode)
        {
            switch (tenantCode)
            {
                case "LRK":
                    return new LarkspurClientSearchForm();
                case "THS":
                case "THS-NORTH":
                    return new ThistleClientSearchForm();
                default:
                    return new ClientSearchForm();
            }
        }

        private static readonly Dictionary<string, Type> ByRegion = new Dictionary<string, Type>
        {
            { "west", typeof(LarkspurClientSearchForm) },
            { "north", typeof(ThistleClientSearchForm) },
        };

        public static ClientSearchForm ForRegion(string region)
        {
            Type type;
            if (!ByRegion.TryGetValue(region, out type)) type = typeof(ClientSearchForm);
            return (ClientSearchForm)Activator.CreateInstance(type);
        }
    }
}
