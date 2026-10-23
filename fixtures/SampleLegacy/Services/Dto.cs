using System;
using System.Collections.Generic;

namespace SampleLegacy.Services
{
    public class PolicyDto
    {
        public int PolicyId { get; set; }
        public int ProductId { get; set; }
        public decimal Premium { get; set; }
        public string Frequency { get; set; }
        public int Installments { get; set; }
        public bool SinglePremium { get; set; }
        public DateTime StartDate { get; set; }
        public List<RiderDto> Riders { get; set; }
    }

    public class RiderDto { public int RiderId { get; set; } public string Name { get; set; } public decimal Loading { get; set; } }
    public class ProductDto { public int ProductId { get; set; } public string Name { get; set; } public decimal MaxPremium { get; set; } public decimal MinPremium { get; set; } }
    public class PremiumLimitsDto { public decimal Min { get; set; } public decimal Max { get; set; } }
    public class ClientDto { public int ClientId { get; set; } public string Surname { get; set; } public string Forename { get; set; } public DateTime DateOfBirth { get; set; } public string Postcode { get; set; } }
    public class PayeeAccountDto { public int AccountId { get; set; } public string Holder { get; set; } public string Iban { get; set; } public bool Domestic { get; set; } }
    public class PayoutCheckDto { public bool Valid { get; set; } public string Reason { get; set; } }
    public class ClientSearchCriteria { public string Surname { get; set; } public string Postcode { get; set; } public DateTime? DateOfBirth { get; set; } public bool IncludeDeceased { get; set; } public int MaxResults { get; set; } }
}
