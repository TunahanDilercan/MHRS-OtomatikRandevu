using MHRS_OtomatikRandevu.Services.Abstracts;
using System.Net.Http;
using System.Text;

namespace MHRS_OtomatikRandevu.Services
{
    public class NotificationService : INotificationService
    {
        // Telegram Bot
        public string? TELEGRAM_BOT_TOKEN = "";
        public string? TELEGRAM_CHAT_ID = "";

        private readonly HttpClient _httpClient;

        public NotificationService()
        {
            _httpClient = new HttpClient();
            
            // .env'den deðerleri oku
            TELEGRAM_BOT_TOKEN = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
            TELEGRAM_CHAT_ID = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "";
        }

        public async Task SendNotification(string message)
        {
            if (!string.IsNullOrEmpty(TELEGRAM_BOT_TOKEN) && !string.IsNullOrEmpty(TELEGRAM_CHAT_ID))
            {
                await SendTelegramMessage(message);
            }
        }

        private async Task SendTelegramMessage(string message)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{TELEGRAM_BOT_TOKEN}/sendMessage";
                var payload = new
                {
                    chat_id = TELEGRAM_CHAT_ID,
                    text = $" MHRS Bot\n\n{message}",
                    parse_mode = "HTML"
                };

                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(url, content);
                
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]  Telegram bildirimi gönderildi");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]  Telegram bildirimi gönderilemedi: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]  Telegram hatasý: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
