using SampleLegacy.Services;

namespace SampleLegacy.Helpers
{
    public static class PremiumRules
    {
        public static bool IsWithinLimits(decimal premium, PremiumLimitsDto limits)
        {
            if (limits == null) return true;
            return premium >= limits.Min && premium <= limits.Max;
        }

        public static int DefaultInstallments(string frequency)
        {
            switch (frequency)
            {
                case "Monthly": return 12;
                case "Quarterly": return 4;
                default: return 1;
            }
        }

        public static decimal ApplyLoading(decimal premium, decimal loadingPercent)
        {
            return premium + premium * loadingPercent / 100m;
        }
    }
}
