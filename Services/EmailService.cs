namespace StripePractice.Services
{
    public class EmailService
    {
        public static void Send(string to, string subject, string body)
        {
            // هنا كود إرسال الإيميل (SMTP أو أي مزود)
            Console.WriteLine($"Email sent to {to}: {subject}");
        }
    }
}
