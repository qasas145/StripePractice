namespace StripePractice.Uitls
{
    public class ProratedAmountCalculator
    {
        public static (int billableMonths, int extraDays, DateTime backdateStart, DateTime billingAnchor)
            CalcAddonProrationWithDates(DateTime now, DateTime mainSubscriptionEnd)
        {
            // 1. احسب الفرق (نفس الكود السابق)
            int monthsDiff = (mainSubscriptionEnd.Year - now.Year) * 12 +
                             Math.Abs(mainSubscriptionEnd.Month - now.Month);
            int daysDiff = mainSubscriptionEnd.Day - now.Day;

            if (daysDiff < 0)
            {
                monthsDiff -= 1;
                var prevMonth = mainSubscriptionEnd.AddMonths(-1);
                int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
                daysDiff += daysInPrevMonth;
            }

            // 2. احسب الشهور القابلة للفوترة
            int billableMonths;
            int extraDays;

            if (monthsDiff <= 0)
            {
                billableMonths = 1;
                extraDays = daysDiff;
            }
            else
            {
                if (daysDiff > 0)
                {
                    billableMonths = monthsDiff + 1;
                    extraDays = daysDiff;
                }
                else
                {
                    billableMonths = monthsDiff;
                    extraDays = 0;
                }
            }

            // 3. احسب billing anchor
            DateTime billingAnchor;
            if (extraDays > 0)
            {
                billingAnchor = mainSubscriptionEnd.AddDays(30 - extraDays + 1);
            }
            else
            {
                billingAnchor = mainSubscriptionEnd;
            }

            // 4. احسب backdate start
            // الفكرة: الاشتراك السنوي "بدأ وهمياً" من (12 - billableMonths) شهور فاتت
            int monthsAlreadyPassed = 12 - billableMonths;

            DateTime backdateStart = now.AddMonths(-monthsAlreadyPassed);

            // اضبط اليوم ليكون نفس يوم الـ billingAnchor
            // عشان الـ cycle يكون aligned
            int targetDay = billingAnchor.Day;
            int daysInBackdateMonth = DateTime.DaysInMonth(backdateStart.Year, backdateStart.Month);

            // لو اليوم أكبر من أيام الشهر، استخدم آخر يوم
            if (targetDay > daysInBackdateMonth)
            {
                targetDay = daysInBackdateMonth;
            }

            backdateStart = new DateTime(
                backdateStart.Year,
                backdateStart.Month,
                targetDay,
                0, 0, 0,
                DateTimeKind.Utc
            );

            Console.WriteLine($"=== Calculation Results ===");
            Console.WriteLine($"Now: {now:yyyy-MM-dd}");
            Console.WriteLine($"Main sub ends: {mainSubscriptionEnd:yyyy-MM-dd}");
            Console.WriteLine($"Months diff: {monthsDiff}, Days diff: {daysDiff}");
            Console.WriteLine($"Billable months: {billableMonths}, Extra days: {extraDays}");
            Console.WriteLine($"Months already passed (virtual): {monthsAlreadyPassed}");
            Console.WriteLine($"Backdate start: {backdateStart:yyyy-MM-dd}");
            Console.WriteLine($"Billing anchor: {billingAnchor:yyyy-MM-dd}");
            Console.WriteLine($"First renewal: {billingAnchor:yyyy-MM-dd}");
            Console.WriteLine($"Second renewal: {billingAnchor.AddYears(1):yyyy-MM-dd}");

            return (billableMonths, extraDays, backdateStart, billingAnchor);
        }
    }
}
