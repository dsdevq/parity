namespace SampleLegacy.Session
{
    // Process-wide state the wizard forms share instead of passing values: one form writes it, another reads it.
    public class WizardSession
    {
        public static WizardSession Current = new WizardSession();

        public int PolicyId { get; set; }
        public int BeneficiaryId { get; set; }
        public string BeneficiaryName { get; set; }
        public int Changes;
    }
}
