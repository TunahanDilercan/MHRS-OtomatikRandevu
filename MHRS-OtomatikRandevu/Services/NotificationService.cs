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
            
            // .env'den degerleri oku
            TELEGRAM_BOT_TOKEN = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim() ?? "";
            TELEGRAM_CHAT_ID = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")?.Trim() ?? "";

            // Eger kullanici token'in basina yanlislikla 'bot' yazdiysa temizle
            if (!string.IsNullOrEmpty(TELEGRAM_BOT_TOKEN) && TELEGRAM_BOT_TOKEN.ToLower().StartsWith("bot") && TELEGRAM_BOT_TOKEN.Length > 3 && char.IsDigit(TELEGRAM_BOT_TOKEN[3]))
            {
                TELEGRAM_BOT_TOKEN = TELEGRAM_BOT_TOKEN.Substring(3);
                Console.WriteLine("[BILGI] Telegram Token basindaki hatali 'bot' oneki otomatik temizlendi.");
            }
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
                // URL'yi olustur
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
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[UYARI]  Telegram bildirimi gonderilemedi: {response.StatusCode}");
                    Console.WriteLine($"[DETAY] Telegram Yaniti: {errorContent}");
                    Console.WriteLine($"[IPUCU] Token veya Chat ID hatali olabilir. Token: {MaskToken(TELEGRAM_BOT_TOKEN)}");
                    
                    if (TELEGRAM_BOT_TOKEN.Contains(" ") || TELEGRAM_BOT_TOKEN.Contains("\n") || TELEGRAM_BOT_TOKEN.Contains("\r"))
                    {
                        Console.WriteLine("[HATA] Telegram Token icinde bosluk veya satir sonu karakteri var! .env dosyasini kontrol edin.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HATA] Telegram servisi hatasi: {ex.Message}");
            }
        }

        private string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 10) return "****";
            return token.Substring(0, 5) + "..." + token.Substring(token.Length - 5);
        }
    }
}
