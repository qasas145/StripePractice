namespace StripePractice.Services
{
    using Microsoft.Extensions.Configuration;
    using Stripe;
    using StripePractice.Uitls;

    public class StripeService
    {
        public StripeService(IConfiguration config)
        {
            var apiKey = config["Stripe:ApiKey"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Stripe:ApiKey is not configured.");
            StripeConfiguration.ApiKey = apiKey;
        }

        public Customer CreateCustomer(string email)
        {
            var service = new CustomerService();
            return service.Create(new CustomerCreateOptions { Email = email });
        }

        public Subscription CreateSubscription(string customerId,
            string priceId,
            string paymentMethodId)
        {
            var paymentMethodService = new PaymentMethodService();
            paymentMethodService.Attach(paymentMethodId,
                new PaymentMethodAttachOptions { Customer = customerId });

            var customerService = new CustomerService();
            customerService.Update(customerId, new CustomerUpdateOptions
            {
                InvoiceSettings = new CustomerInvoiceSettingsOptions
                {
                    DefaultPaymentMethod = paymentMethodId
                }
            });

            var subscriptionService = new SubscriptionService();
            return subscriptionService.Create(new SubscriptionCreateOptions
            {
                Customer = customerId,
                DefaultPaymentMethod = paymentMethodId,
                PaymentBehavior = "default_incomplete", // create invoice + PaymentIntent that must be confirmed
                Items = new List<SubscriptionItemOptions>
                {
                    new SubscriptionItemOptions { Price = priceId }
                },
                PaymentSettings = new SubscriptionPaymentSettingsOptions
                {
                    SaveDefaultPaymentMethod = "on_subscription",
                    PaymentMethodTypes = new List<string> { "card" }
                }
            });
        }

        public Subscription CancelSubscription(string subscriptionId, bool atPeriodEnd = false)
        {
            var service = new SubscriptionService();
            if (atPeriodEnd)
            {
                return service.Update(subscriptionId, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true });
            }
            return service.Cancel(subscriptionId, new SubscriptionCancelOptions());
        }

        public (Subscription Subscription, PaymentIntent PaymentIntent) ConfirmSubscriptionPayment(
            string subscriptionId,
            string paymentMethodId)
        {
            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("Subscription ID is required", nameof(subscriptionId));

            if (string.IsNullOrWhiteSpace(paymentMethodId))
                throw new ArgumentException("Payment method ID is required", nameof(paymentMethodId));

            var subscriptionService = new SubscriptionService();
            var subscription = subscriptionService.Get(subscriptionId, new SubscriptionGetOptions
            {
                Expand = new List<string> {  "latest_invoice","latest_invoice.payment_intent" }
            });

            if (subscription == null)
                throw new InvalidOperationException("Subscription not found");

            var invoiceId = subscription.LatestInvoice?.Id ?? subscription.LatestInvoiceId;
            if (string.IsNullOrWhiteSpace(invoiceId))
                throw new InvalidOperationException("Subscription has no latest invoice to pay");

            var invoiceService = new InvoiceService();
            var invoice = invoiceService.Get(invoiceId, new InvoiceGetOptions
            {
                Expand = new List<string> { "payments.data.payment.payment_intent" }
            });

            string? paymentIntentId = invoice.Payments?
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Payment?.PaymentIntentId))?
                .Payment?.PaymentIntentId
                ?? subscription.LatestInvoice?.Payments?
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Payment?.PaymentIntentId))?
                    .Payment?.PaymentIntentId;

            if (string.IsNullOrWhiteSpace(paymentIntentId))
                throw new InvalidOperationException(
                    "Latest invoice does not have a payment intent; this usually means no payment is due yet (e.g. a free trial).");

            var paymentIntentService = new PaymentIntentService();
            var paymentIntent = paymentIntentService.Confirm(paymentIntentId, new PaymentIntentConfirmOptions
            {
                PaymentMethod = paymentMethodId,
                PaymentMethodOptions = new PaymentIntentPaymentMethodOptionsOptions
                {
                    Card = new PaymentIntentPaymentMethodOptionsCardOptions
                    {
                        RequestThreeDSecure = "challenge"
                    }
                }
            });

            var refreshedSubscription = subscriptionService.Get(subscriptionId);

            return (refreshedSubscription, paymentIntent);
        }

        /// <summary>
        /// Change subscription plan (upgrade or downgrade).
        /// </summary>
        /// <param name="subscriptionId">Current subscription ID</param>
        /// <param name="newPriceId">New price ID to switch to</param>
        /// <param name="prorationBehavior">
        /// How to handle proration:
        /// - "create_prorations" (default): Immediate change with prorated charges/credits
        /// - "none": Immediate change, no proration (full price from next billing)
        /// - "always_invoice": Immediate change with immediate invoice for proration
        /// - "pending_if_incomplete": Apply pending if current invoice incomplete
        /// </param>
        /// <param name="billingCycleAnchor">
        /// When to reset billing cycle:
        /// - "unchanged" (default): Keep current billing date
        /// - "now": Reset billing cycle to now (new period starts immediately)
        /// </param>
        /// <returns>Updated subscription details with proration info</returns>
        public ChangePlanResult ChangePlan(
            string subscriptionId,
            string newPriceId,
            string prorationBehavior = "create_prorations",
            string billingCycleAnchor = "unchanged")
        {
            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("Subscription ID is required", nameof(subscriptionId));

            if (string.IsNullOrWhiteSpace(newPriceId))
                throw new ArgumentException("New Price ID is required", nameof(newPriceId));

            var subscriptionService = new SubscriptionService();

            // Fetch current subscription to get the item ID
            var currentSubscription = subscriptionService.Get(subscriptionId, new SubscriptionGetOptions
            {
                Expand = new List<string> { "items.data.price" }
            });

            if (currentSubscription == null)
                throw new InvalidOperationException("Subscription not found");

            var currentItem = currentSubscription.Items?.Data?.FirstOrDefault();
            if (currentItem == null)
                throw new InvalidOperationException("Subscription has no items");

            var oldPriceId = currentItem.Price?.Id;
            var oldPriceAmount = currentItem.Price?.UnitAmount ?? 0;

            // Fetch new price to determine if upgrade or downgrade
            var priceService = new PriceService();
            var newPrice = priceService.Get(newPriceId);
            var newPriceAmount = newPrice?.UnitAmount ?? 0;

            var isUpgrade = newPriceAmount > oldPriceAmount;
            var isDowngrade = newPriceAmount < oldPriceAmount;

            // Build update options
            var updateOptions = new SubscriptionUpdateOptions
            {
                Items = new List<SubscriptionItemOptions>
                {
                    new SubscriptionItemOptions
                    {
                        Id = currentItem.Id,
                        Price = newPriceId
                    }
                },
                ProrationBehavior = prorationBehavior
            };

            // Handle billing cycle anchor
            if (billingCycleAnchor == "now")
            {
                updateOptions.BillingCycleAnchor = SubscriptionBillingCycleAnchor.Now;
            }

            // If upgrading and wanting immediate payment, use always_invoice
            if (isUpgrade && prorationBehavior == "create_prorations")
            {
                updateOptions.ProrationBehavior = "always_invoice";
            }

            // Update the subscription
            var updatedSubscription = subscriptionService.Update(subscriptionId, updateOptions);

            // Get the current period end from subscription item (v48+ API change)
            var updatedItem = updatedSubscription.Items?.Data?.FirstOrDefault();
            var currentPeriodEnd = updatedItem?.CurrentPeriodEnd ?? DateTime.UtcNow.AddMonths(1);

            return new ChangePlanResult
            {
                Subscription = updatedSubscription,
                OldPriceId = oldPriceId,
                NewPriceId = newPriceId,
                IsUpgrade = isUpgrade,
                IsDowngrade = isDowngrade,
                ProratedAmountDue = null, // Proration preview removed in v48+
                EffectiveDate = billingCycleAnchor == "now" 
                    ? DateTime.UtcNow 
                    : currentPeriodEnd
            };
        }

        /// <summary>
        /// Preview what a plan change would cost (proration preview).
        /// Note: In Stripe.net v48+, Invoice.Upcoming was removed. 
        /// This method uses Invoice.CreatePreview instead.
        /// </summary>
        public ProrationPreview PreviewPlanChange(string subscriptionId, string newPriceId)
        {
            if (string.IsNullOrWhiteSpace(subscriptionId))
                throw new ArgumentException("Subscription ID is required", nameof(subscriptionId));

            if (string.IsNullOrWhiteSpace(newPriceId))
                throw new ArgumentException("New Price ID is required", nameof(newPriceId));

            var subscriptionService = new SubscriptionService();
            var currentSubscription = subscriptionService.Get(subscriptionId, new SubscriptionGetOptions
            {
                Expand = new List<string> { "items.data.price" }
            });

            if (currentSubscription == null)
                throw new InvalidOperationException("Subscription not found");

            var currentItem = currentSubscription.Items?.Data?.FirstOrDefault();
            if (currentItem == null)
                throw new InvalidOperationException("Subscription has no items");

            var oldPriceId = currentItem.Price?.Id;
            var oldPriceAmount = currentItem.Price?.UnitAmount ?? 0;

            // Fetch new price
            var priceService = new PriceService();
            var newPrice = priceService.Get(newPriceId);
            var newPriceAmount = newPrice?.UnitAmount ?? 0;

            // Get current period end from subscription item (v48+ API change)
            var currentPeriodEnd = currentItem.CurrentPeriodEnd;

            // In v48+, Invoice.Upcoming was removed. Use CreatePreview instead.
            var invoiceService = new InvoiceService();
            Invoice? previewInvoice = null;
            long proratedCredits = 0;
            long proratedCharges = 0;
            long immediateAmountDue = 0;

            try
            {
                previewInvoice = invoiceService.CreatePreview(new InvoiceCreatePreviewOptions
                {
                    Customer = currentSubscription.CustomerId,
                    SubscriptionDetails = new InvoiceSubscriptionDetailsOptions
                    {
                        Items = new List<InvoiceSubscriptionDetailsItemOptions>
                        {
                            new InvoiceSubscriptionDetailsItemOptions
                            {
                                Id = currentItem.Id,
                                Price = newPriceId
                            }
                        },
                        ProrationBehavior = "create_prorations"
                    }
                });

                proratedCredits = previewInvoice.Lines?.Data?
                    .Where(l => l.Amount < 0)
                    .Sum(l => l.Amount) ?? 0;

                proratedCharges = previewInvoice.Lines?.Data?
                    .Where(l => l.Amount > 0)
                    .Sum(l => l.Amount) ?? 0;

                immediateAmountDue = previewInvoice.AmountDue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create invoice preview: {ex.Message}");
                // Calculate simple estimate based on price difference
                var priceDiff = newPriceAmount - oldPriceAmount;
                if (priceDiff > 0)
                    proratedCharges = priceDiff;
                else
                    proratedCredits = priceDiff;
                immediateAmountDue = Math.Max(0, priceDiff);
            }

            return new ProrationPreview
            {
                OldPriceId = oldPriceId,
                NewPriceId = newPriceId,
                OldPriceAmount = oldPriceAmount,
                NewPriceAmount = newPriceAmount,
                IsUpgrade = newPriceAmount > oldPriceAmount,
                IsDowngrade = newPriceAmount < oldPriceAmount,
                ProratedCredits = proratedCredits,
                ProratedCharges = proratedCharges,
                ImmediateAmountDue = immediateAmountDue,
                NextBillingDate = currentPeriodEnd,
                Currency = previewInvoice?.Currency ?? "usd"
            };
        }

        /// <summary>
        /// Get subscription details including current plan info.
        /// </summary>
        public Subscription GetSubscription(string subscriptionId)
        {
            var subscriptionService = new SubscriptionService();
            return subscriptionService.Get(subscriptionId, new SubscriptionGetOptions
            {
                Expand = new List<string> { "items.data.price", "latest_invoice" }
            });
        }

        /// <summary>
        /// حساب الـ proration amount للـ annual subscription
        /// - عدد الأيام من تاريخ الاشتراك إلى Anniversary Date / 365 * السعر السنوي الكامل
        /// - مثلاً: اشترك يوم 16 أكتوبر، يدفع من 16 أكتوبر لحد 15 يناير السنة الجديدة
        /// - التجديد التالي يكون يوم 16 يناير
        /// </summary>
        public long CalculateAnnualProratedAmount(long annualPriceAmount, DateTime? subscriptionStartDate = null)
        {
            // إذا ما في تاريخ محدد، استخدم اليوم الحالي
            var startDate = subscriptionStartDate ?? DateTime.UtcNow.Date;
            
            // تاريخ التجديد = نفس اليوم والشهر من السنة الجديدة
            var anniversaryDate = new DateTime(startDate.Year + 1, startDate.Month, startDate.Day);
            
            // عدد الأيام المتبقية
            var daysRemaining = (anniversaryDate - startDate).TotalDays + 1; // +1 لتشمل اليوم الأول
            
            // حساب النسبة المتبقية من السنة (365 يوم)
            var prorationRatio = daysRemaining / 365.0;
            
            // حساب المبلغ النسبي (نقرب للأسفل للعملة)
            var proratedAmount = (long)Math.Floor(annualPriceAmount * prorationRatio);
            
            return proratedAmount;
        }

        /// <summary>
        /// تحديد ما إذا كان subscription هو annual نوع أم لا (موسمي)
        /// </summary>
        public bool IsAnnualSubscriptionBilling(string subscriptionId)
        {
            var subscriptionService = new SubscriptionService();
            var subscription = subscriptionService.Get(subscriptionId, new SubscriptionGetOptions
            {
                Expand = new List<string> { "items.data.price" }
            });

            if (subscription?.Items?.Data?.Count > 0)
            {
                var item = subscription.Items.Data[0];
                var price = item.Price;

                // التحقق من metadata أو البيانات اللي تحدد نوع الـ billing
                if (price?.Metadata?.ContainsKey("is_annual") == true)
                {
                    return price.Metadata["is_annual"].ToString().ToLower() == "true";
                }

                // أو يمكن التحقق من billing period (12 months = annual)
                if (price?.Recurring != null)
                {
                    // إذا كان recurring interval = month و interval count = 12
                    if (price.Recurring.Interval == "month" && 
                        price.Recurring.IntervalCount == 12)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// إنشاء annual subscription مع حساب proration
        /// عند الاشتراك في منتصف السنة، يتم حساب السعر فقط للأيام المتبقية
        /// باستخدام backdateStart مباشرة دون الحاجة لعمل invoice يدوي
        /// </summary>
        public Subscription CreateAnnualSubscriptionWithProration(
            string customerId,
            string priceId,
            string paymentMethodId,
            DateTime? subscriptionStartDate = null,
            long? annualPriceAmount = null,
            DateTime? mainSubscriptionEnd = null)
        {
            var paymentMethodService = new PaymentMethodService();
            paymentMethodService.Attach(paymentMethodId,
                new PaymentMethodAttachOptions { Customer = customerId });

            var customerService = new CustomerService();
            customerService.Update(customerId, new CustomerUpdateOptions
            {
                InvoiceSettings = new CustomerInvoiceSettingsOptions
                {
                    DefaultPaymentMethod = paymentMethodId
                }
            });

            var now = subscriptionStartDate ?? DateTime.UtcNow.Date;
            var mainSubEnd = new DateTime(now.Year, 5, 31);

            // احسب التواريخ والشهور باستخدام CalcAddonProrationWithDates
            var (billableMonths, extraDays, backdateStart, billingAnchor) = 
                ProratedAmountCalculator.CalcAddonProrationWithDates(now, mainSubEnd);

            // إنشاء الاشتراك باستخدام backdateStart مباشرة
            var subscriptionOptions = new SubscriptionCreateOptions
            {
                Customer = customerId,
                DefaultPaymentMethod = paymentMethodId,
                Items = new List<SubscriptionItemOptions>
                {
                    new SubscriptionItemOptions { Price = priceId }
                },
                //BillingCycleAnchor = billingAnchor.AddMonths(-10),
                ProrationBehavior = "create_prorations",
                CollectionMethod = "charge_automatically",
                BillingCycleAnchor = DateTime.SpecifyKind(now.AddMinutes(5), DateTimeKind.Utc),

                BackdateStartDate = backdateStart.AddYears(-1)
            };

            var subscriptionService = new SubscriptionService();
            var subscription = subscriptionService.Create(subscriptionOptions);

            return subscriptionService.Get(subscription.Id);
        }
    }

    public class ChangePlanResult
    {
        public Subscription Subscription { get; set; } = null!;
        public string? OldPriceId { get; set; }
        public string? NewPriceId { get; set; }
        public bool IsUpgrade { get; set; }
        public bool IsDowngrade { get; set; }
        public long? ProratedAmountDue { get; set; }
        public DateTime EffectiveDate { get; set; }
    }

    public class ProrationPreview
    {
        public string? OldPriceId { get; set; }
        public string? NewPriceId { get; set; }
        public long OldPriceAmount { get; set; }
        public long NewPriceAmount { get; set; }
        public bool IsUpgrade { get; set; }
        public bool IsDowngrade { get; set; }
        public long ProratedCredits { get; set; }
        public long ProratedCharges { get; set; }
        public long ImmediateAmountDue { get; set; }
        public DateTime? NextBillingDate { get; set; }
        public string? Currency { get; set; }
    }

}
