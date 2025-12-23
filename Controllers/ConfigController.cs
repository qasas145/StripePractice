using Microsoft.AspNetCore.Mvc;

namespace StripeUseExample.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConfigController : ControllerBase
    {
        private readonly IConfiguration _config;

        public ConfigController(IConfiguration config)
        {
            _config = config;
        }

        [HttpGet("stripe-publishable-key")]
        public IActionResult GetStripePublishableKey()
        {
            var publishableKey = _config["Stripe:PublishableKey"];
            if (string.IsNullOrWhiteSpace(publishableKey))
                return NotFound("Stripe publishable key is not configured.");

            return Ok(new { publishableKey });
        }
    }
}
