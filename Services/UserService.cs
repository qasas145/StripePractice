using StripeUseExample.Data;
using StripeUseExample.Models;

namespace StripeUseExample.Services
{
    public class UserService
    {
        private readonly AppDbContext _db;

        public UserService(AppDbContext db) => _db = db;

        public bool CanSendEmail(User user)
        {
            var plan = GetPlan(user.Plan);
            return user.EmailsSentThisMonth < plan.MonthlyEmailLimit;
        }

        public void SendEmail(User user, string to, string subject, string body)
        {
            if (!CanSendEmail(user))
                throw new Exception("Email limit reached for this month!");

            EmailService.Send(to, subject, body);
            user.EmailsSentThisMonth++;
            _db.SaveChanges();
        }

        private Plan GetPlan(string planName)
        {
            // افترض Plan محفوظة في DB
            return _db.Plans.First(p => p.Name == planName);
        }
    }
}
