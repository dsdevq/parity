using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using SampleLegacy.Helpers;
using SampleLegacy.Resources;

namespace SampleLegacy.Models
{
    public class PremiumHistoryRow
    {
        [Browsable(false)]
        public int HistoryId { get; set; }

        [Display(ResourceType = typeof(Captions), Name = "colEffectiveDate")]
        [Required]
        public DateTime EffectiveDate { get; set; }

        [Display(ResourceType = typeof(Captions), Name = "colReason")]
        [Required]
        [StringLength(PolicyLimits.ReasonMaxLength)]
        public string Reason { get; set; }

        [Display(Name = "Amount")]
        public decimal Amount { get; set; }
    }
}
