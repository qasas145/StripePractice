using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using StripePractice.Data;
using StripePractice.Models;
using StripePractice.Services;
using StripePractice.Uitls;

namespace StripePractice.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class StripeController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly StripeService _stripe;

        public StripeController(AppDbContext db, StripeService stripe)
        {
            _db = db;
            _stripe = stripe;
        }

        public class CreateCustomerRequest
        {
            public string Email { get; set; }
        }

        [HttpPost("customer")]
        public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email)) return BadRequest("Email is required");

            var existing = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (existing != null && !string.IsNullOrWhiteSpace(existing.StripeCustomerId))
            {
                return Ok(new { existing.Id, existing.Email, existing.StripeCustomerId });
            }

            var customer = _stripe.CreateCustomer(req.Email);

            if (existing == null)
            {
                var trialPlan = await _db.Plans.FirstOrDefaultAsync(p => p.Name == "FreeTrial");
                var trialEndsAt = DateTime.UtcNow.AddDays(14);
                existing = new User
                {
                    Email = req.Email,
                    StripeCustomerId = customer.Id,
                    StripeSubscriptionId = null,
                    Plan = trialPlan?.Name ?? "FreeTrial",
                    SubscriptionStatus = "Trial",
                    HasPremiumFeatures = true,
                    EmailsSentThisMonth = 0,
                    SubscriptionEndDate = trialEndsAt
                };
                _db.Users.Add(existing);
            }
            else
            {
                existing.StripeCustomerId = customer.Id;
            }

            await _db.SaveChangesAsync();
            return Ok(new { existing.Id, existing.Email, existing.StripeCustomerId });
        }

        public class CreateSubscriptionRequest
        {
            public string Email { get; set; }
            public string PriceId { get; set; }
            public string PaymentMethodId { get; set; }
        }

        // this is for version 41.7.1 with stripe ,
        //[HttpGet("GetSubId")]
        //public async Task<IActionResult> GetSubscription()
        //{
        //    var chargeId = "ch_3SgtM3IjuXMYVJ7Q0UF0Fvq9";
        //    var chargeService = new ChargeService();
        //    var charge = chargeService.Get(chargeId, null, null);

        //    // Get invoice to find subscription
        //    var invoiceService = new InvoiceService();
        //    var invoice = invoiceService.Get(charge.InvoiceId, null, null);

        //    //if (invoice?.SubscriptionId == null)
        //    //    return false;
        //    return Ok();

        //}

        [HttpPost("subscription")]
        public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.PriceId))
                return BadRequest("Email and PriceId are required");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (user == null) return NotFound("User not found");
            if (string.IsNullOrWhiteSpace(user.StripeCustomerId)) return BadRequest("User has no Stripe customer");

            if (string.IsNullOrWhiteSpace(req.PaymentMethodId))
                return BadRequest("PaymentMethodId is required to create a subscription");

            var sub = _stripe.CreateSubscription(user.StripeCustomerId, req.PriceId, req.PaymentMethodId);

            user.StripeSubscriptionId = sub.Id; // final status handled by webhook
            var plan = await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == req.PriceId);
            if (plan != null)
            {
                user.Plan = plan.Name;
            }
            await _db.SaveChangesAsync();

            return Ok(new
            {
                user.Id,
                user.Email,
                user.Plan,
                user.StripeSubscriptionId,
                StripeSubscriptionStatus = sub.Status
            });
        }

        public class CancelSubscriptionRequest
        {
            public string SubscriptionId { get; set; }
            public bool AtPeriodEnd { get; set; }
        }

        public class CompleteSubscriptionPaymentRequest
        {
            public string SubscriptionId { get; set; }
            public string PaymentMethodId { get; set; }
            public string? Email { get; set; }
        }

        [HttpPost("subscription/cancel")]
        public IActionResult CancelSubscription([FromBody] CancelSubscriptionRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.SubscriptionId)) return BadRequest("SubscriptionId is required");

            var result = _stripe.CancelSubscription(req.SubscriptionId, req.AtPeriodEnd);

            return Ok(new { result.Id, result.Status, result.CancelAtPeriodEnd });
        }

        [HttpPost("subscription/pay")]
        public async Task<IActionResult> CompleteSubscriptionPayment([FromBody] CompleteSubscriptionPaymentRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.SubscriptionId) || string.IsNullOrWhiteSpace(req.PaymentMethodId))
            {
                return BadRequest("SubscriptionId and PaymentMethodId are required");
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.StripeSubscriptionId == req.SubscriptionId);

            if (user == null && !string.IsNullOrWhiteSpace(req.Email))
            {
                user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            }

            if (user == null)
            {
                return NotFound("User not found");
            }

            try
            {
                var result = _stripe.ConfirmSubscriptionPayment(req.SubscriptionId, req.PaymentMethodId);

                user.StripeSubscriptionId ??= result.Subscription.Id;

                var newPriceId = result.Subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
                var plan = !string.IsNullOrWhiteSpace(newPriceId)
                    ? await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == newPriceId)
                    : null;
                if (plan != null)
                {
                    user.Plan = plan.Name;
                }

                var mappedStatus = MapSubscriptionStatus(result.Subscription);
                user.SubscriptionStatus = mappedStatus.Status;
                user.HasPremiumFeatures = mappedStatus.HasPremium;
                user.SubscriptionEndDate = mappedStatus.SubscriptionEnd;

                await _db.SaveChangesAsync();

                return Ok(new
                {
                    user.Id,
                    user.Email,
                    user.Plan,
                    user.StripeSubscriptionId,
                    SubscriptionStatus = user.SubscriptionStatus,
                    HasPremium = user.HasPremiumFeatures,
                    StripeSubscriptionStatus = result.Subscription.Status,
                    PaymentIntentStatus = result.PaymentIntent.Status,
                    ClientSecret = result.PaymentIntent.ClientSecret,
                    NextAction = result.PaymentIntent.NextAction?.Type
                });
            }
            catch (StripeException ex)
            {
                Console.WriteLine(ex.Message);
                return BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        private static (string Status, bool HasPremium, DateTime? SubscriptionEnd) MapSubscriptionStatus(Subscription subscription)
        {
            var status = subscription.Status;
            var cancelAtPeriodEnd = subscription.CancelAtPeriodEnd;
            var subscriptionEnd = subscription.EndedAt ?? subscription.CancelAt ?? subscription.TrialEnd;

            if (status == "canceled")
            {
                return ("Canceled", false, subscriptionEnd);
            }

            if (cancelAtPeriodEnd)
            {
                return ("PendingCancel", true, subscription.CancelAt ?? subscriptionEnd);
            }

            if (status == "trialing")
            {
                return ("Trial", true, subscription.TrialEnd ?? subscriptionEnd);
            }

            if (status == "past_due" || status == "unpaid")
            {
                return ("PastDue", false, subscriptionEnd);
            }

            if (status == "incomplete" || status == "incomplete_expired")
            {
                return ("Incomplete", false, subscriptionEnd);
            }

            return ("Active", true, subscriptionEnd);
        }

        #region Plan Change (Upgrade/Downgrade)

        public class ChangePlanRequest
        {
            /// <summary>User email to identify the subscription</summary>
            public string? Email { get; set; }

            /// <summary>Subscription ID (optional if Email provided)</summary>
            public string? SubscriptionId { get; set; }

            /// <summary>New price ID to switch to</summary>
            public string NewPriceId { get; set; } = null!;

            /// <summary>
            /// Proration behavior:
            /// - "create_prorations" (default): Immediate change with prorated charges/credits applied to next invoice
            /// - "always_invoice": Immediate change with immediate invoice for proration
            /// - "none": No proration, full new price from next billing cycle
            /// </summary>
            public string ProrationBehavior { get; set; } = "create_prorations";

            /// <summary>
            /// Whether to reset billing cycle:
            /// - false (default): Keep current billing date
            /// - true: Reset billing cycle to now
            /// </summary>
            public bool ResetBillingCycle { get; set; } = false;
        }

        public class PreviewPlanChangeRequest
        {
            public string? Email { get; set; }
            public string? SubscriptionId { get; set; }
            public string NewPriceId { get; set; } = null!;
        }

        /// <summary>
        /// Preview what a plan change would cost before applying it.
        /// Shows proration credits, charges, and immediate amount due.
        /// </summary>
        [HttpPost("subscription/preview-change")]
        public async Task<IActionResult> PreviewPlanChange([FromBody] PreviewPlanChangeRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.NewPriceId))
                return BadRequest("NewPriceId is required");

            var subscriptionId = req.SubscriptionId;

            if (string.IsNullOrWhiteSpace(subscriptionId) && !string.IsNullOrWhiteSpace(req.Email))
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
                subscriptionId = user?.StripeSubscriptionId;
            }

            if (string.IsNullOrWhiteSpace(subscriptionId))
                return BadRequest("SubscriptionId or valid Email is required");

            try
            {
                var preview = _stripe.PreviewPlanChange(subscriptionId, req.NewPriceId);

                var oldPlan = await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == preview.OldPriceId);
                var newPlan = await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == preview.NewPriceId);

                return Ok(new
                {
                    OldPlan = oldPlan?.Name,
                    NewPlan = newPlan?.Name,
                    preview.OldPriceId,
                    preview.NewPriceId,
                    OldPriceAmount = preview.OldPriceAmount / 100.0m, // Convert cents to currency
                    NewPriceAmount = preview.NewPriceAmount / 100.0m,
                    preview.IsUpgrade,
                    preview.IsDowngrade,
                    ProratedCredits = preview.ProratedCredits / 100.0m,
                    ProratedCharges = preview.ProratedCharges / 100.0m,
                    ImmediateAmountDue = preview.ImmediateAmountDue / 100.0m,
                    preview.NextBillingDate,
                    preview.Currency
                });
            }
            catch (StripeException ex)
            {
                return BadRequest(new { Error = ex.Message, Code = ex.StripeError?.Code });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }

        /// <summary>
        /// Change subscription plan (upgrade or downgrade).
        /// Handles proration automatically based on ProrationBehavior.
        /// </summary>
        [HttpPost("subscription/change-plan")]
        public async Task<IActionResult> ChangePlan([FromBody] ChangePlanRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.NewPriceId))
                return BadRequest("NewPriceId is required");

            var subscriptionId = req.SubscriptionId;
            User? user = null;

            if (!string.IsNullOrWhiteSpace(req.Email))
            {
                user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
                subscriptionId ??= user?.StripeSubscriptionId;
            }

            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                return BadRequest("SubscriptionId or valid Email is required");
            }

            if (user == null)
            {
                user = await _db.Users.FirstOrDefaultAsync(u => u.StripeSubscriptionId == subscriptionId);
            }

            if (user == null)
            {
                return NotFound("User not found");
            }

            // Validate new plan exists
            var newPlan = await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == req.NewPriceId);
            if (newPlan == null)
            {
                return BadRequest("Invalid NewPriceId - plan not found");
            }

            try
            {
                var billingAnchor = req.ResetBillingCycle ? "now" : "unchanged";

                var result = _stripe.ChangePlan(
                    subscriptionId,
                    req.NewPriceId,
                    req.ProrationBehavior,
                    billingAnchor
                );

                // Update local user record
                var oldPlan = user.Plan;
                user.Plan = newPlan.Name;

                var mappedStatus = MapSubscriptionStatus(result.Subscription);
                user.SubscriptionStatus = mappedStatus.Status;
                user.HasPremiumFeatures = mappedStatus.HasPremium;
                // Get CurrentPeriodEnd from subscription item (v49+ API change)
                var subscriptionItem = result.Subscription.Items?.Data?.FirstOrDefault();
                var currentPeriodEnd = subscriptionItem?.CurrentPeriodEnd;
                user.SubscriptionEndDate = currentPeriodEnd;

                await _db.SaveChangesAsync();

                var oldPlanRecord = await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == result.OldPriceId);

                return Ok(new
                {
                    Success = true,
                    user.Id,
                    user.Email,
                    OldPlan = oldPlanRecord?.Name ?? oldPlan,
                    NewPlan = newPlan.Name,
                    result.IsUpgrade,
                    result.IsDowngrade,
                    ProratedAmountDue = result.ProratedAmountDue.HasValue 
                        ? result.ProratedAmountDue.Value / 100.0m 
                        : (decimal?)null,
                    EffectiveDate = result.EffectiveDate,
                    SubscriptionStatus = user.SubscriptionStatus,
                    StripeSubscriptionStatus = result.Subscription.Status,
                    CurrentPeriodEnd = currentPeriodEnd
                });
            }
            catch (StripeException ex)
            {
                Console.WriteLine($"Stripe error changing plan: {ex.Message}");
                return BadRequest(new { Error = ex.Message, Code = ex.StripeError?.Code });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error changing plan: {ex.Message}");
                return BadRequest(new { Error = ex.Message });
            }
        }

        /// <summary>
        /// Get current subscription details including plan info.
        /// </summary>
        [HttpGet("subscription/{email}")]
        public async Task<IActionResult> GetSubscriptionDetails(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest("Email is required");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user == null)
                return NotFound("User not found");

            if (string.IsNullOrWhiteSpace(user.StripeSubscriptionId))
            {
                return Ok(new
                {
                    user.Id,
                    user.Email,
                    user.Plan,
                    user.SubscriptionStatus,
                    user.HasPremiumFeatures,
                    user.SubscriptionEndDate,
                    StripeSubscription = (object?)null,
                    Message = "No active Stripe subscription"
                });
            }

            try
            {
                var subscription = _stripe.GetSubscription(user.StripeSubscriptionId);

                var currentPriceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
                var currentPlan = !string.IsNullOrWhiteSpace(currentPriceId)
                    ? await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == currentPriceId)
                    : null;

                // Get all available plans for upgrade/downgrade options
                var allPlans = await _db.Plans
                    .Where(p => p.StripePriceId != null && p.StripePriceId != currentPriceId)
                    .Select(p => new { p.Name, p.StripePriceId, p.MonthlyEmailLimit })
                    .ToListAsync();

                // Get CurrentPeriodEnd from subscription item (v49+ API change)
                var subscriptionItem = subscription.Items?.Data?.FirstOrDefault();

                return Ok(new
                {
                    user.Id,
                    user.Email,
                    CurrentPlan = currentPlan?.Name ?? user.Plan,
                    CurrentPriceId = currentPriceId,
                    user.SubscriptionStatus,
                    user.HasPremiumFeatures,
                    user.EmailsSentThisMonth,
                    MonthlyEmailLimit = currentPlan?.MonthlyEmailLimit,
                    SubscriptionEndDate = user.SubscriptionEndDate,
                    CurrentPeriodEnd = subscriptionItem?.CurrentPeriodEnd,
                    TrialEnd = subscription.TrialEnd,
                    CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
                    StripeStatus = subscription.Status,
                    AvailablePlans = allPlans
                });
            }
            catch (StripeException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }

        #endregion

        #region Annual Subscription with Proration

        /// <summary>
        /// نموذج طلب الاشتراك السنوي مع الحساب النسبي
        /// </summary>
        public class CreateAnnualSubscriptionRequest
        {
            /// <summary>البريد الإلكتروني للمستخدم</summary>
            public string Email { get; set; } = null!;

            /// <summary>معرف السعر في Stripe (يجب أن يكون سعر سنوي)</summary>
            public string PriceId { get; set; } = null!;

            /// <summary>معرف طريقة الدفع</summary>
            public string PaymentMethodId { get; set; } = null!;

            /// <summary>
            /// تاريخ بداية الاشتراك (اختياري، إذا تركت فارغ سيستخدم اليوم الحالي)
            /// عند الاشتراك يوم 10 يونيو مثلاً، يتم حساب السعر فقط للأيام المتبقية إلى 31 ديسمبر
            /// </summary>
            public DateTime? SubscriptionStartDate { get; set; }

            /// <summary>
            /// المبلغ السنوي الكامل بالسنتات (cents)
            /// مثلاً: إذا السعر السنوي $1200 = 120000 سنت
            /// سيتم حساب نسبة المبلغ بناءً على الأيام المتبقية من السنة
            /// </summary>
            public long? AnnualPriceAmount { get; set; }

            /// <summary>
            /// تاريخ بداية الفوترة المستقبلية (التاريخ اللي هيبدأ فيه الاشتراك بالسعر الكامل)
            /// سيتم إضافة ExtraDays عليه تلقائياً
            /// </summary>
            public DateTime? FutureBillingStartDate { get; set; }
        }

        /// <summary>
        /// Detect إذا كان السعر annual أم شهري أم غيره
        /// </summary>
        public class SubscriptionTypeResponse
        {
            public string PriceId { get; set; } = null!;
            public string BillingPeriod { get; set; } = null!; // "monthly", "annual", "other"
            public long UnitAmount { get; set; }
            public string Currency { get; set; } = null!;
            public bool IsAnnual { get; set; }
            public string? IntervalCount { get; set; } // عدد الفترات (مثلاً 12 شهر = annual)
            public string Message { get; set; } = null!;
        }

        /// <summary>
        /// GET: التحقق من نوع الاشتراك (سنوي أم شهري)
        /// يتم استخدام هذا للتحقق من نوع السعر قبل الاشتراك
        /// </summary>
        [HttpGet("subscription-type/{priceId}")]
        public async Task<IActionResult> GetSubscriptionType(string priceId)
        {
            if (string.IsNullOrWhiteSpace(priceId))
                return BadRequest("PriceId is required");

            try
            {
                var priceService = new PriceService();
                var price = priceService.Get(priceId);

                if (price == null)
                    return NotFound("Price not found");

                var recurring = price.Recurring;
                var isAnnual = false;
                var billingPeriod = "other";
                var message = "";

                if (recurring != null)
                {
                    if (recurring.Interval == "month")
                    {
                        if (recurring.IntervalCount == 12)
                        {
                            isAnnual = true;
                            billingPeriod = "annual";
                            message = "هذا سعر سنوي (شهري * 12) - سيتم حساب نسبة المبلغ إذا تم الاشتراك قبل 31 ديسمبر";
                        }
                        else if (recurring.IntervalCount == 1)
                        {
                            billingPeriod = "monthly";
                            message = "هذا سعر شهري عادي";
                        }
                        else
                        {
                            billingPeriod = "other";
                            message = $"هذا سعر كل {recurring.IntervalCount} أشهر";
                        }
                    }
                    else if (recurring.Interval == "year")
                    {
                        isAnnual = true;
                        billingPeriod = "annual";
                        message = "هذا سعر سنوي حقيقي - سيتم حساب نسبة المبلغ إذا تم الاشتراك قبل 31 ديسمبر";
                    }
                    else if (recurring.Interval == "day")
                    {
                        billingPeriod = "daily";
                        message = "هذا سعر يومي";
                    }
                    else if (recurring.Interval == "week")
                    {
                        billingPeriod = "weekly";
                        message = "هذا سعر أسبوعي";
                    }
                }

                return Ok(new SubscriptionTypeResponse
                {
                    PriceId = priceId,
                    BillingPeriod = billingPeriod,
                    UnitAmount = price.UnitAmount ?? 0,
                    Currency = price.Currency?.ToUpper() ?? "USD",
                    IsAnnual = isAnnual,
                    IntervalCount = recurring?.IntervalCount.ToString(),
                    Message = message
                });
            }
            catch (StripeException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }

        /// <summary>
        /// POST: إنشاء اشتراك سنوي مع حساب الـ proration
        /// إذا اشترك يوم 10 يونيو، سيدفع فقط من 10 يونيو لحد 31 ديسمبر (نسبة من السعر السنوي)
        /// ثم من 1 يناير السنة الجديدة، سيتم تحديثه لدفع السعر السنوي الكامل
        /// </summary>
        [HttpPost("subscription/annual-with-proration")]
        public async Task<IActionResult> CreateAnnualSubscriptionWithProration(
            [FromBody] CreateAnnualSubscriptionRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || 
                string.IsNullOrWhiteSpace(req.PriceId) || 
                string.IsNullOrWhiteSpace(req.PaymentMethodId))
            {
                return BadRequest("Email, PriceId, and PaymentMethodId are required");
            }

            try
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
                if (user == null)
                    return NotFound("User not found");

                if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
                    return BadRequest("User has no Stripe customer");

                // التحقق من نوع السعر (يجب أن يكون سنوي)
                var priceService = new PriceService();
                var price = priceService.Get(req.PriceId);

                if (price == null)
                    return NotFound("Price not found");

                // التحقق من أن السعر سنوي
                bool isAnnualPrice = false;
                long? annualAmount = null;

                if (price.Recurring != null)
                {
                    if ((price.Recurring.Interval == "month" && price.Recurring.IntervalCount == 12) ||
                        price.Recurring.Interval == "year")
                    {
                        isAnnualPrice = true;
                        annualAmount = price.UnitAmount ?? req.AnnualPriceAmount;
                    }
                }

                if (!isAnnualPrice)
                    return BadRequest("This price is not an annual price. Please use a monthly or annual recurring price.");

                var subscriptionStartDate = req.SubscriptionStartDate ?? DateTime.UtcNow.Date;

                // حساب تاريخ التجديد (Anniversary)
                var anniversaryDate = new DateTime(subscriptionStartDate.Year, 12, 31);

                // إنشاء الاشتراك مع الـ proration - استخدم الميثود الجديد
                var subscription = _stripe.CreateAnnualSubscriptionWithProration(
                    user.StripeCustomerId,
                    req.PriceId,
                    req.PaymentMethodId,
                    subscriptionStartDate,
                    annualAmount,
                    anniversaryDate);

                // تحديث بيانات المستخدم
                user.StripeSubscriptionId = subscription.Id;

                var plan = await _db.Plans.FirstOrDefaultAsync(p => p.StripePriceId == req.PriceId);
                if (plan != null)
                {
                    user.Plan = plan.Name;
                }

                await _db.SaveChangesAsync();

                // استخدم CalcAddonProrationWithDates للحصول على جميع التفاصيل المطلوبة
                var (billableMonths, extraDays, backdateStart, billingAnchor) = 
                    ProratedAmountCalculator.CalcAddonProrationWithDates(subscriptionStartDate, anniversaryDate);

                
                var daysRemaining = (anniversaryDate - subscriptionStartDate).TotalDays + 1;

                return Ok("Congratulations Prorated");
            }
            catch (StripeException ex)
            {
                Console.WriteLine($"Stripe error: {ex.Message}");
                return BadRequest(new { Error = ex.Message, Code = ex.StripeError?.Code });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return BadRequest(new { Error = ex.Message });
            }
        }

        #endregion

    }
}

