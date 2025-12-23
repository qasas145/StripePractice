namespace StripeUseExample.Models
{
    public class Plan
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int MonthlyEmailLimit { get; set; }
        public string? StripePriceId { get; set; }
    }
}
