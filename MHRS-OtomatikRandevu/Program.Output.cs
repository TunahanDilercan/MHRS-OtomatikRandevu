using MHRS_OtomatikRandevu.Models;
using MHRS_OtomatikRandevu.Models.RequestModels;
using MHRS_OtomatikRandevu.Models.ResponseModels;
using MHRS_OtomatikRandevu.Services;
using MHRS_OtomatikRandevu.Services.Abstracts;
using MHRS_OtomatikRandevu.Urls;
using MHRS_OtomatikRandevu.Utils;
using MHRS_OtomatikRandevu.Exceptions;
using System.Net;
using System.Globalization;   // saat kontrolü için


namespace MHRS_OtomatikRandevu
{
    // Loglama ve çıkış
    public partial class Program
    {
        /// <summary>Mesajı yazar/loglar, istenirse Telegram'a gönderir (bekler) ve verilen kodla çıkar.</summary>
        static void ExitWith(int code, string message, bool notify)
        {
            Console.WriteLine($"[EXIT {code}] {message}");
            LogStatus($"Çıkış ({code}): {message}", null, false);
            if (notify && _notificationService != null)
            {
                try { _notificationService.SendNotification(message).Wait(TimeSpan.FromSeconds(20)); } catch { }
            }
            Environment.Exit(code);
        }


        static void LogStatus(string status, string? slotTime = null, bool showConsole = false)
        {
            var logPath = Path.Combine(Directory.GetCurrentDirectory(), LOG_FILE_NAME);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] {status}";
            if (!string.IsNullOrEmpty(slotTime))
                logLine += $" | Slot: {slotTime}";
            
            // Dosyaya her zaman yaz
            try
            {
                File.AppendAllText(logPath, logLine + Environment.NewLine);
            }
            catch
            {
                // Log dosyası yazılamıyorsa sessizce devam et
            }
            
            // Konsola sadece önemli mesajları yazdır
            if (showConsole)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status}");
                if (!string.IsNullOrEmpty(slotTime))
                    Console.WriteLine($"             📅 Slot: {slotTime}");
            }
        }
    }
}
