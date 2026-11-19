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
    public partial class Program
    {
        static string? TC_NO;
        static string? SIFRE;
        static int TELEGRAM_NOTIFY_FREQUENCY = 10;
        // Tarih aralığı: yalnızca bu aralıktaki (ve geçmemiş) slotlar alınır.
        // SLOT_MAX yalnızca MHRS_END_DATE verilmişse sınırlıdır; geçince bot durur.
        static DateTime SLOT_MIN = DateTime.MinValue;
        static DateTime SLOT_MAX = DateTime.MaxValue;
        // MHRS_CONTINUOUS=1: saat pencerelerini beklemeden sürekli ara
        static bool CONTINUOUS = false;
        // Normal modda iki deneme arası bekleme (sn). Varsayılan 120 = önceki davranış.
        static int INTERVAL_SECONDS = 120;
        static string? ENV_START_DATE;
        static List<string> ENV_SCHEDULE_TIMES = new();

        const string TOKEN_FILE_NAME = "token.txt";
        const string LOG_FILE_NAME = "randevu_log.txt";
        static string? JWT_TOKEN;
        static DateTime TOKEN_END_DATE;

        static IClientService? _client;
        static INotificationService? _notificationService;

        static string? QUERY_START;   // MHRS slot sorgusu başlangıcı (yyyy-MM-dd HH:mm:ss)
        static string? QUERY_END;     // MHRS slot sorgusu bitişi
        static bool DAILY_REPORT = true;
        static bool DRY_RUN;   // MHRS_DRY_RUN=1: randevu alma adımı hiç çalışmaz

        static void Main(string[] args)
        {
            // Setup modu kontrolü
            if (args.Length > 0 && args[0] == "--setup")
            {
                RunSetupWizard();
                return;
            }

            // Çıkış kodları: 0 = randevu alındı, 1 = geçici hata (systemd yeniden başlatır),
            // 2 = kalıcı hata / yapılandırma hatası / iş bitti (systemd yeniden başlatMAZ).
            // Main içindeki her "return" varsayılan olarak 2 ile çıkar.
            Environment.ExitCode = 2;

            LoadEnvFile(".env");

            _client = new ClientService();
            _notificationService = new NotificationService();

            // Önceki başarılı randevu kontrolü
            var successFilePath = Path.Combine(Directory.GetCurrentDirectory(), "randevu_basarili.txt");
            if (File.Exists(successFilePath))
            {
                Console.WriteLine("⚠️  UYARI: Daha önce başarılı bir randevu alınmış (randevu_basarili.txt mevcut).");
                Console.WriteLine("Bot, mükerrer randevu almamak için durduruluyor.");
                return;
            }

            // Ayarları oku ve doğrula: tüm hatalar tek seferde, MHRS'ye giriş yapmadan önce
            var (cfg, errors) = BotConfig.FromEnvironment();
            foreach (var w in cfg.Warnings)
                Console.WriteLine($"[UYARI] {w}");
            if (errors.Count > 0)
            {
                Console.WriteLine("[ERROR] .env ayarlarında hata var:");
                foreach (var e in errors)
                    Console.WriteLine($"  - {e}");
                return;
            }
            ApplyConfig(cfg);
            Console.WriteLine(TOKEN_MODE
                ? "[INFO] Token modu: şifreyle giriş yapılmayacak, tarayıcıdan alınan oturum kullanılacak."
                : "[INFO] TC ve şifre çevre değişkenlerinden yüklendi.");
            if (ENV_SCHEDULE_TIMES.Count > 0)
                Console.WriteLine($"[INFO] Özel zamanlama aktif: {string.Join(", ", ENV_SCHEDULE_TIMES)}");

            // Telegram komutları (isteğe bağlı): yalnızca kayıtlı sohbetten gelen komutlar işlenir.
            // Girişten ÖNCE başlar: token modunda ilk oturum /token komutuyla gelebilir.
            var tg = _notificationService as NotificationService;
            if (cfg.TelegramCommands && tg != null && !string.IsNullOrEmpty(tg.TELEGRAM_BOT_TOKEN) && !string.IsNullOrEmpty(tg.TELEGRAM_CHAT_ID))
            {
                new TelegramCommandListener(tg.TELEGRAM_BOT_TOKEN!, tg.TELEGRAM_CHAT_ID!, HandleCommand, tg.SendNotification).Start();
                COMMANDS_ACTIVE = true;
            }
            else if (TOKEN_MODE)
                Console.WriteLine("[UYARI] Token modu açık ama Telegram komutları kapalı: oturumu token.txt'ye elle yazmanız gerekecek.");

            // ÖNCE LOGIN YAP VE JWT TOKEN AL
            Console.WriteLine("[INFO] MHRS sistemine giriş yapılıyor...");
            var loginResult = GetToken(_client!);
            if (loginResult == null || string.IsNullOrEmpty(loginResult.Token))
            {
                Console.WriteLine("[ERROR] MHRS sistemine giriş yapılamadı! TC kimlik ve şifrenizi kontrol edin.");
                ExitForLoginFailure("ilk giriş");
                return;
            }
            ApplyToken(loginResult);
            Console.WriteLine("[INFO] MHRS sistemine başarıyla giriş yapıldı.");

            var target = ResolveTarget(cfg);
            if (target == null)
                return;   // hata mesajı ResolveTarget içinde yazıldı
            CURRENT_TARGET = target;

            RunSearchLoop(target);
        }

        /// <summary>.env dosyasını ortam değişkenlerine yükler (# yorum ve boş satırlar atlanır).</summary>
        static void LoadEnvFile(string path)
        {
            if (!File.Exists(path))
                return;
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                    Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
            }
        }

        static void ApplyConfig(BotConfig cfg)
        {
            TC_NO = cfg.Tc;
            SIFRE = cfg.Password;
            TELEGRAM_NOTIFY_FREQUENCY = cfg.NotifyFrequency;
            CONTINUOUS = cfg.Continuous;
            INTERVAL_SECONDS = cfg.IntervalSeconds;
            ENV_SCHEDULE_TIMES = cfg.ScheduleTimes;
            DAILY_REPORT = cfg.DailyReport;
            TOKEN_MODE = cfg.TokenMode;
            DRY_RUN = cfg.DryRun;
            if (DRY_RUN)
                Console.WriteLine("[UYARI] DENEME MODU (MHRS_DRY_RUN=1): slot bulunsa bile randevu ALINMAYACAK.");
            lock (DATE_LOCK)
            {
                ENV_START_DATE = cfg.StartDate.ToString("dd-MM-yyyy");
                SLOT_MIN = cfg.StartDate;
                SLOT_MAX = cfg.EndDate ?? DateTime.MaxValue;
                QUERY_START = cfg.StartDate.ToString("yyyy-MM-dd HH:mm:ss");
                QUERY_END = (cfg.EndDate ?? DateTime.Now.AddDays(12)).ToString("yyyy-MM-dd HH:mm:ss");   // bitiş yoksa bugünden 12 gün
            }
        }
    }
}
