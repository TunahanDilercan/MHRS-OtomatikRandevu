using MHRS_OtomatikRandevu.Services.Abstracts;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using MHRS_OtomatikRandevu.Utils;
using System.Net.Http;
using System.Text;
using System.Net.Mail;
using System.Net;

namespace MHRS_OtomatikRandevu.Services
{
    public class NotificationService : INotificationService
    {
        // Twilio SMS
        public string? TWILIO_ACCOUNT_SID = "";
        public string? TWILIO_AUTH_TOKEN = "";
        public string? TWILIO_PHONE_NUMBER = "";
        public string? PHONE_NUMBER = "";

        // Telegram Bot
        public string? TELEGRAM_BOT_TOKEN = "";
        public string? TELEGRAM_CHAT_ID = "";

        // Email
        public string? EMAIL_SMTP_HOST = "";
        public string? EMAIL_SMTP_PORT = "";
        public string? EMAIL_USERNAME = "";
        public string? EMAIL_PASSWORD = "";
        public string? EMAIL_TO = "";

        private readonly HttpClient _httpClient;

        public NotificationService()
        {
            _httpClient = new HttpClient();
            
            // .env'den değerleri oku
            TELEGRAM_BOT_TOKEN = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
            TELEGRAM_CHAT_ID = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "";
            EMAIL_SMTP_HOST = Environment.GetEnvironmentVariable("EMAIL_SMTP_HOST") ?? "";
            EMAIL_SMTP_PORT = Environment.GetEnvironmentVariable("EMAIL_SMTP_PORT") ?? "";
            EMAIL_USERNAME = Environment.GetEnvironmentVariable("EMAIL_USERNAME") ?? "";
            EMAIL_PASSWORD = Environment.GetEnvironmentVariable("EMAIL_PASSWORD") ?? "";
            EMAIL_TO = Environment.GetEnvironmentVariable("EMAIL_TO") ?? "";
            
            // Twilio değerlerini .env'den oku
            TWILIO_ACCOUNT_SID = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") ?? "";
            TWILIO_AUTH_TOKEN = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? "";
            TWILIO_PHONE_NUMBER = Environment.GetEnvironmentVariable("TWILIO_PHONE_NUMBER") ?? "";
            PHONE_NUMBER = Environment.GetEnvironmentVariable("PHONE_NUMBER") ?? "";
            
            if (!IsConfigEmpty())
            {
                try
                {
                    TwilioClient.Init(TWILIO_ACCOUNT_SID, TWILIO_AUTH_TOKEN);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARNING] Twilio init error: {ex.Message}");
                }
            }
        }

        private bool IsConfigEmpty()
        {
            return string.IsNullOrEmpty(TWILIO_ACCOUNT_SID) || string.IsNullOrEmpty(TWILIO_AUTH_TOKEN) 
                                                            || string.IsNullOrEmpty(TWILIO_PHONE_NUMBER) 
                                                            || string.IsNullOrEmpty(PHONE_NUMBER);
        }

        public async Task SendNotification(string message)
        {
            var tasks = new List<Task>();

            // Telegram bildirim gönder
            if (!string.IsNullOrEmpty(TELEGRAM_BOT_TOKEN) && !string.IsNullOrEmpty(TELEGRAM_CHAT_ID))
            {
                tasks.Add(SendTelegramMessage(message));
            }

            // Email bildirim gönder
            if (!string.IsNullOrEmpty(EMAIL_SMTP_HOST) && !string.IsNullOrEmpty(EMAIL_TO))
            {
                tasks.Add(SendEmailMessage(message));
            }

            // SMS bildirim gönder (Twilio)
            if (!IsConfigEmpty())
            {
                tasks.Add(SendSmsMessage(message));
            }

            // Tüm bildirimleri paralel gönder
            await Task.WhenAll(tasks);
        }

        private async Task SendTelegramMessage(string message)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{TELEGRAM_BOT_TOKEN}/sendMessage";
                var payload = new
                {
                    chat_id = TELEGRAM_CHAT_ID,
                    text = $"🤖 MHRS Bot\n\n{message}",
                    parse_mode = "HTML"
                };

                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(url, content);
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Telegram bildirimi gönderildi");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Telegram bildirimi gönderilemedi: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Telegram hatası: {ex.Message}");
            }
        }

        private async Task SendEmailMessage(string message)
        {
            try
            {
                using var smtpClient = new SmtpClient(EMAIL_SMTP_HOST);
                
                if (!string.IsNullOrEmpty(EMAIL_SMTP_PORT) && int.TryParse(EMAIL_SMTP_PORT, out int port))
                {
                    smtpClient.Port = port;
                }
                
                smtpClient.Credentials = new NetworkCredential(EMAIL_USERNAME, EMAIL_PASSWORD);
                smtpClient.EnableSsl = true;

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(EMAIL_USERNAME ?? ""),
                    Subject = "🤖 MHRS Bot Bildirimi",
                    Body = $"MHRS Bot Bildirimi\n\nTarih: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n{message}",
                    IsBodyHtml = false
                };
                
                mailMessage.To.Add(EMAIL_TO ?? "");

                await smtpClient.SendMailAsync(mailMessage);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Email bildirimi gönderildi");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Email hatası: {ex.Message}");
            }
        }

        private async Task SendSmsMessage(string message)
        {
            try
            {
                await MessageResource.CreateAsync(
                    from: new Twilio.Types.PhoneNumber(TWILIO_PHONE_NUMBER),
                    to: new Twilio.Types.PhoneNumber(PHONE_NUMBER),
                    body: message
                );
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ SMS bildirimi gönderildi");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ SMS hatası: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
