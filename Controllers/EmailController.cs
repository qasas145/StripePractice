using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StripeUseExample.Data;
using StripeUseExample.Services;

namespace StripeUseExample.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EmailController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly UserService _userService;

        public EmailController(AppDbContext db, UserService userService)
        {
            _db = db;
            _userService = userService;
        }

        public class SendEmailRequest
        {
            public string UserEmail { get; set; }
            public string To { get; set; }
            public string Subject { get; set; }
            public string Body { get; set; }
        }

        [HttpPost("send")]
        public async Task<IActionResult> Send([FromBody] SendEmailRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.UserEmail) || string.IsNullOrWhiteSpace(req.To))
            {
                return BadRequest("UserEmail and To are required.");
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.UserEmail);
            if (user == null)
            {
                return NotFound("User not found.");
            }

            // Allow send only for active or trial users
            var status = user.SubscriptionStatus?.ToLowerInvariant();
            var allowedStatus = status == "active" || status == "trial";
            if (!allowedStatus)
            {
                return Forbid("Subscription is not active or trial.");
            }

            try
            {
                _userService.SendEmail(user, req.To, req.Subject ?? string.Empty, req.Body ?? string.Empty);
                return Ok(new
                {
                    user.EmailsSentThisMonth,
                    Limit = _db.Plans.First(p => p.Name == user.Plan).MonthlyEmailLimit,
                    Message = "Email sent successfully"
                });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
