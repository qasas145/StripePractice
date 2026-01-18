namespace StripePractice.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Email { get; set; }
        public string StripeCustomerId { get; set; }
        public string? StripeSubscriptionId { get; set; }
        public string Plan { get; set; } // Basic, Premium
        public int EmailsSentThisMonth { get; set; }
        public bool HasPremiumFeatures { get; set; }
        public string SubscriptionStatus { get; set; } // Active, PendingCancel, Canceled, Trial
        public DateTime? SubscriptionEndDate { get; set; }
    }
}
