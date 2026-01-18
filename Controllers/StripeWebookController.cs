using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Stripe;
using StripePractice.Data;
using StripePractice.Models;
using StripePractice.Services;
using Newtonsoft.Json.Linq;

namespace StripePractice.Controllers
{
    [Route("stripe/webhook")]
    [ApiController]
    public class StripeWebhookController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly string _webhookSecret;

        public StripeWebhookController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _webhookSecret = config["Stripe:WebhookSecret"] ?? string.Empty;
            var apiKey = config["Stripe:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                StripeConfiguration.ApiKey = apiKey;
            }
        }

        [HttpPost]
        public async Task<IActionResult> Index()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret
            );

            switch (stripeEvent.Type)
            {
                case "customer.subscription.deleted":
                case "customer.subscription.updated":
                    HandleUpdatedSubscription(stripeEvent.Data.Object as Subscription);
                    break;

                case "customer.subscription.created":
                    HandleCreatedSubscription(stripeEvent.Data.Object as Subscription);
                    break;

                case "invoice.payment_succeeded":
                case "invoice.paid":
                case "invoice_payment.paid":
                    HandleInvoiceSucceeded(stripeEvent.Data.Object as Invoice);
                    break;

                case "invoice.payment_failed":
                    HandleInvoiceFailed(stripeEvent.Data.Object as Invoice);
                    break;

                case "invoice.voided":
                    HandleInvoiceVoided(stripeEvent.Data.Object as Invoice);
                    break;

                case "charge.succeeded":
                    HandleChargeSucceeded(stripeEvent.Data.Object as Charge);
                    break;

                case "payment_intent.succeeded":
                    HandlePaymentIntentSucceeded(stripeEvent.Data.Object as PaymentIntent);
                    break;

                case "payment_intent.canceled":
                    HandlePaymentIntentCanceled(stripeEvent.Data.Object as PaymentIntent);
                    break;

                default:
                    Console.WriteLine($"Unhandled event type: {stripeEvent.Type}");
                    break;
            }

            return Ok();
        }

        private void HandleUpdatedSubscription(Subscription subscription)
        {
            if (subscription == null) return;
            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == subscription.CustomerId);
            if (user == null) return;

            user.StripeSubscriptionId ??= subscription.Id;

            var newPriceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
            var plan = !string.IsNullOrWhiteSpace(newPriceId)
                ? _db.Plans.FirstOrDefault(p => p.StripePriceId == newPriceId)
                : null;

            if (plan != null)
            {
                user.Plan = plan.Name;
            }

            var status = subscription.Status;
            var cancelAtPeriodEnd = subscription.CancelAtPeriodEnd;

            var subscriptionEnd = subscription.EndedAt ?? subscription.CancelAt ?? subscription.TrialEnd;
            var hasPremium = true;
            var subscriptionStatus = "Active";
            var subject = "Subscription Updated";
            var body = $"اشتراكك تم تحديثه. باقتك الحالية: {user.Plan}";

            if (status == "canceled")
            {
                subscriptionStatus = "Canceled";
                hasPremium = false;
                subscriptionEnd = subscription.EndedAt ?? subscriptionEnd;
                subject = "Subscription Cancelled";
                body = $"تم إلغاء الاشتراك وإيقاف الميزات. تاريخ الانتهاء: {subscriptionEnd?.ToLocalTime()}";
            }
            else if (cancelAtPeriodEnd)
            {
                subscriptionStatus = "PendingCancel";
                hasPremium = true;
                subscriptionEnd = subscription.CancelAt ?? subscriptionEnd;
                subject = "Subscription Cancellation Scheduled";
                body = $"تم جدولة إلغاء الاشتراك في {subscriptionEnd?.ToLocalTime()}. ستظل الميزات فعالة حتى هذا التاريخ.";
            }
            else if (status == "trialing")
            {
                subscriptionStatus = "Trial";
                hasPremium = true;
                subscriptionEnd = subscription.TrialEnd ?? subscriptionEnd;
                subject = "Trial Active";
                body = $"أنت في فترة تجربة. تنتهي في {subscriptionEnd?.ToLocalTime()}";
            }
            else if (status == "past_due" || status == "unpaid")
            {
                subscriptionStatus = "PastDue";
                hasPremium = false;
                subject = "Payment Overdue";
                body = "الفاتورة متأخرة أو غير مدفوعة. الميزات موقوفة حتى يتم السداد.";
            }
            else if (status == "incomplete" || status == "incomplete_expired")
            {
                subscriptionStatus = "Incomplete";
                hasPremium = false;
                subject = "Subscription Incomplete";
                body = "هناك مشكلة في إعداد الاشتراك. يرجى تحديث وسيلة الدفع أو إعادة المحاولة.";
            }
            else
            {
                subscriptionStatus = "Active";
                hasPremium = true;
                subject = "Subscription Updated";
                body = $"تم تحديث الاشتراك. خطتك الحالية: {user.Plan}";
            }

            user.SubscriptionStatus = subscriptionStatus;
            user.HasPremiumFeatures = hasPremium;
            user.SubscriptionEndDate = subscriptionEnd;

            _db.SaveChanges();

            EmailService.Send(user.Email, subject, body);
        }

        private void HandleCreatedSubscription(Subscription subscription)
        {
            if (subscription == null) return;
            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == subscription.CustomerId);
            if (user == null) return;

            user.StripeSubscriptionId = subscription.Id;

            var priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
            var plan = !string.IsNullOrWhiteSpace(priceId)
                ? _db.Plans.FirstOrDefault(p => p.StripePriceId == priceId)
                : null;
            if (plan != null)
            {
                user.Plan = plan.Name;
            }

            var status = subscription.Status;
            var subscriptionStatus = "Active";
            var hasPremium = true;
            var subscriptionEnd = subscription.TrialEnd ?? subscription.CancelAt ?? subscription.EndedAt;
            var subject = "Subscription Created";
            var body = $"تم إنشاء اشتراكك. الحالة الحالية: {status}, الخطة: {user.Plan}";

            if (status == "trialing")
            {
                subscriptionStatus = "Trial";
                hasPremium = true;
                subscriptionEnd = subscription.TrialEnd ?? subscriptionEnd;
                subject = "Trial Started";
                body = $"أنت الآن في فترة تجربة. تنتهي في {subscriptionEnd?.ToLocalTime()}";
            }
            else if (status == "canceled")
            {
                subscriptionStatus = "Canceled";
                hasPremium = false;
                subscriptionEnd = subscription.EndedAt ?? subscriptionEnd;
                subject = "Subscription Cancelled";
                body = $"تم إلغاء الاشتراك. تاريخ الانتهاء: {subscriptionEnd?.ToLocalTime()}";
            }
            else if (status == "past_due" || status == "unpaid")
            {
                subscriptionStatus = "PastDue";
                hasPremium = false;
                subject = "Payment Required";
                body = "الدفع مطلوب لإكمال تفعيل الاشتراك.";
            }
            else if (status == "incomplete" || status == "incomplete_expired")
            {
                subscriptionStatus = "Incomplete";
                hasPremium = false;
                subject = "Subscription Incomplete";
                body = "الاشتراك لم يكتمل بعد. يرجى إتمام الدفع.";
            }

            user.SubscriptionStatus = subscriptionStatus;
            user.HasPremiumFeatures = hasPremium;
            user.SubscriptionEndDate = subscriptionEnd;

            _db.SaveChanges();

            EmailService.Send(user.Email, subject, body);
        }

        private void HandleInvoiceVoided(Invoice invoice)
        {
            if (invoice == null || string.IsNullOrWhiteSpace(invoice.CustomerId)) return;

            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == invoice.CustomerId);
            if (user == null) return;

            user.SubscriptionStatus = "Canceled";
            user.HasPremiumFeatures = false;
            user.SubscriptionEndDate = invoice.StatusTransitions?.VoidedAt;

            _db.SaveChanges();

            EmailService.Send(user.Email, "Invoice Voided", "تم إلغاء الفاتورة وإيقاف الميزات.");
        }

        private void HandleChargeSucceeded(Charge charge)
        {
            if (charge == null || string.IsNullOrWhiteSpace(charge.CustomerId)) return;

            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == charge.CustomerId);
            if (user == null) return;

            user.SubscriptionStatus = "Active";
            user.HasPremiumFeatures = true;

            _db.SaveChanges();

            EmailService.Send(user.Email, "Charge Succeeded", "تم الدفع بنجاح وتم تفعيل الميزات.");

            Console.WriteLine($"charge.succeeded chargeId: {charge.Id}");
        }

        private void HandlePaymentIntentSucceeded(PaymentIntent intent)
        {
            if (intent == null || string.IsNullOrWhiteSpace(intent.CustomerId)) return;

            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == intent.CustomerId);
            if (user == null) return;

            user.SubscriptionStatus = "Active";
            user.HasPremiumFeatures = true;

            _db.SaveChanges();

            EmailService.Send(user.Email, "Payment Intent Succeeded", "تم تأكيد الدفع وتم تفعيل الميزات.");

            Console.WriteLine($"payment_intent.succeeded latest_charge: {intent.LatestChargeId}");
        }

        private void HandlePaymentIntentCanceled(PaymentIntent intent)
        {
            if (intent == null || string.IsNullOrWhiteSpace(intent.CustomerId)) return;

            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == intent.CustomerId);
            if (user == null) return;

            user.SubscriptionStatus = "Canceled";
            user.HasPremiumFeatures = false;

            _db.SaveChanges();

            EmailService.Send(user.Email, "Payment Intent Cancelled", "تم إلغاء محاولة الدفع وتم إيقاف الميزات.");
        }

        private void HandleInvoiceSucceeded(Invoice invoice)
        {
            if (invoice == null || string.IsNullOrWhiteSpace(invoice.CustomerId)) return;

            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == invoice.CustomerId);
            if (user == null) return;

            var chargeId = GetChargeIdFromInvoice(invoice);

            var subscriptionId = user.StripeSubscriptionId ?? string.Empty;

            Subscription? subscription = null;
            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                var subscriptionService = new SubscriptionService();
                subscription = subscriptionService.Get(subscriptionId);
            }

            var isTrialing = subscription?.Status == "trialing";
            var isTrialInvoice = invoice.BillingReason == "subscription_create" && invoice.AmountPaid == 0;
            var trialEnd = subscription?.TrialEnd;

            if (isTrialing || isTrialInvoice)
            {
                // Stripe sends a $0 invoice when a trial starts; keep the user in trial until it ends.
                user.SubscriptionStatus = "Trial";
                user.HasPremiumFeatures = true;
                user.SubscriptionEndDate = trialEnd ?? user.SubscriptionEndDate;
                _db.SaveChanges();
                return;
            }

            user.SubscriptionStatus = "Active";
            user.HasPremiumFeatures = true;
            // Reset usage monthly could be done by a scheduled job; here we reset on success of new period
            if (invoice.BillingReason == "subscription_cycle")
            {
                user.EmailsSentThisMonth = 0;
            }
            _db.SaveChanges();

            EmailService.Send(user.Email, "Payment Succeeded", "تم دفع الفاتورة بنجاح وتم تفعيل الميزات.");

            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                Console.WriteLine($"invoice.payment_succeeded charge: {chargeId}");
            }
        }

        private void HandleInvoiceFailed(Invoice invoice)
        {
            if (invoice == null || string.IsNullOrWhiteSpace(invoice.CustomerId)) return;

            var user = _db.Users.FirstOrDefault(u => u.StripeCustomerId == invoice.CustomerId);
            if (user == null) return;

            var chargeId = GetChargeIdFromInvoice(invoice);

            user.SubscriptionStatus = "PastDue";
            user.HasPremiumFeatures = false; // optionally keep grace period if needed
            _db.SaveChanges();

            EmailService.Send(user.Email, "Payment Failed", "فشل دفع الفاتورة. تم إيقاف الميزات مؤقتًا.");

            if (!string.IsNullOrWhiteSpace(chargeId))
            {
                Console.WriteLine($"invoice.payment_failed charge: {chargeId}");
            }
        }

        private string GetChargeIdFromInvoice(Invoice invoice)
        {
            // Stripe.net v49 does not expose PaymentIntentId/ChargeId on Invoice, so we extract from raw JSON then fetch the PaymentIntent
            var paymentIntentId = invoice.RawJObject?[(object)"payment_intent"]?.ToString();
            if (string.IsNullOrWhiteSpace(paymentIntentId)) return string.Empty;

            try
            {
                var piService = new PaymentIntentService();
                var pi = piService.Get(paymentIntentId);
                return pi?.LatestChargeId ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch PaymentIntent {paymentIntentId}: {ex.Message}");
                return string.Empty;
            }
        }
    }

}
