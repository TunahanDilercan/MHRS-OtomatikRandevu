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


        const long LOG_MAX_BYTES = 5 * 1024 * 1024;   // 5 MB
        const int LOG_KEEP = 3;                       // randevu_log.txt.1 .. .3

        /// <summary>Log dosyası 5 MB'ı geçince .1, .2, .3 olarak kaydırır (en eskisi silinir).</summary>
        internal static void RotateLogIfNeeded(string logPath, long maxBytes = LOG_MAX_BYTES, int keep = LOG_KEEP)
        {
            var fi = new FileInfo(logPath);
            if (!fi.Exists || fi.Length < maxBytes)
                return;
            for (int i = keep; i >= 1; i--)
            {
                var src = i == 1 ? logPath : $"{logPath}.{i - 1}";
                var dst = $"{logPath}.{i}";
                if (File.Exists(src))
                    File.Move(src, dst, overwrite: true);
            }
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
                RotateLogIfNeeded(logPath);
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
