using System.Globalization;
using MHRS_OtomatikRandevu.Utils;

namespace MHRS_OtomatikRandevu
{
    // Telegram komutları (/durum, /duraklat, /devam, /tarih, /hedef) ve botun canlı durumu
    public partial class Program
    {
        static readonly object DATE_LOCK = new();
        static volatile bool PAUSED;
        static SearchTarget? CURRENT_TARGET;
        static readonly DateTime STARTED_AT = DateTime.Now;
        static int STATE_ATTEMPTS;
        static DateTime STATE_LAST_ATTEMPT;
        static string STATE_LAST_RESULT = "henüz deneme yok";
        static int STATE_ERRORS_TOTAL;

        const string HELP_TEXT =
            "Komutlar:\n" +
            "/durum - deneme sayısı, son sonuç, tarih aralığı\n" +
            "/duraklat - aramayı duraklat\n" +
            "/devam - aramaya devam et\n" +
            "/tarih GG-AA-YYYY [GG-AA-YYYY] - tarih aralığını değiştir (tek tarih = sadece o gün)\n" +
            "/hedef - aranan il/klinik/hastane/doktor ID'leri\n" +
            "/token <değer> - (token modu) tarayıcıdaki MHRS oturumunu bota ver\n\n" +
            "Token nasıl alınır (bilgisayarda): mhrs.gov.tr'ye e-Nabız/e-Devlet ile girin → F12 → Console'a\n" +
            "copy(localStorage.getItem('token-v-mhrs'))\n" +
            "yazın → panodaki U2FsdGVkX1... değerini /token ile gönderin. Göndermeden önce mhrs.gov.tr'den ÇIKIŞ YAPMAYIN; " +
            "sonra telefondan/tarayıcıdan tekrar MHRS'ye girerseniz botun oturumu düşer.";

        /// <summary>"/tarih@BotAdi 18-11-2026" → ("tarih", ["18-11-2026"]). Komut değilse null.</summary>
        internal static (string Command, string[] Args)? ParseCommand(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith("/"))
                return null;
            var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].TrimStart('/');
            var at = cmd.IndexOf('@');
            if (at >= 0) cmd = cmd[..at];
            return (cmd.ToLowerInvariant(), parts.Skip(1).ToArray());
        }

        /// <summary>/tarih argümanlarını okur: tek tarih = o gün; iki tarih = aralık (bitiş günü dahil).</summary>
        internal static bool TryParseDateRangeArgs(string[] args, DateTime today, out DateTime start, out DateTime endInclusive, out string error)
        {
            start = endInclusive = default;
            error = "";
            if (args.Length is < 1 or > 2)
            {
                error = "Kullanım: /tarih GG-AA-YYYY [GG-AA-YYYY]";
                return false;
            }
            if (!TryParseTrDate(args[0], out var s))
            {
                error = $"Geçersiz tarih: {args[0]} (GG-AA-YYYY)";
                return false;
            }
            var e = s;
            if (args.Length == 2 && !TryParseTrDate(args[1], out e))
            {
                error = $"Geçersiz tarih: {args[1]} (GG-AA-YYYY)";
                return false;
            }
            if (e < s)
            {
                error = "Bitiş tarihi başlangıçtan önce olamaz.";
                return false;
            }
            if (e < today.Date)
            {
                error = "Bitiş tarihi geçmişte.";
                return false;
            }
            start = s.Date;
            endInclusive = e.Date.AddDays(1).AddSeconds(-1);
            return true;
        }

        static void SetDateRange(DateTime start, DateTime endInclusive)
        {
            lock (DATE_LOCK)
            {
                SLOT_MIN = start;
                SLOT_MAX = endInclusive;
                ENV_START_DATE = start.ToString("dd-MM-yyyy");
                QUERY_START = start.ToString("yyyy-MM-dd HH:mm:ss");
                QUERY_END = endInclusive.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        static string DateRangeText()
        {
            lock (DATE_LOCK)
                return SLOT_MAX == DateTime.MaxValue
                    ? $"{SLOT_MIN:dd.MM.yyyy} - {QUERY_END} (bitiş yok)"
                    : $"{SLOT_MIN:dd.MM.yyyy} - {SLOT_MAX:dd.MM.yyyy} (bitince bot durur)";
        }

        internal static string HandleCommand(string text)
        {
            var parsed = ParseCommand(text);
            if (parsed == null)
                return HELP_TEXT;
            var (cmd, args) = parsed.Value;

            switch (cmd)
            {
                case "durum":
                    var last = STATE_LAST_ATTEMPT == default ? "-" : $"{STATE_LAST_ATTEMPT:dd.MM HH:mm:ss}";
                    return $"📊 Durum: {(PAUSED ? "⏸ DURAKLATILDI" : "🔍 aranıyor")}{(DRY_RUN ? " (🧪 deneme modu, randevu alınmaz)" : "")}\n" +
                           $"Çalışma: {DateTime.Now - STARTED_AT:d\\.hh\\:mm} (g.ss:dd)\n" +
                           $"Deneme: {STATE_ATTEMPTS} (toplam hata: {STATE_ERRORS_TOTAL})\n" +
                           $"Son deneme: {last} → {STATE_LAST_RESULT}\n" +
                           $"Tarih: {DateRangeText()}\n" +
                           $"Mod: {(CONTINUOUS ? "sürekli" : "saat pencereleri")}, her {INTERVAL_SECONDS} sn" +
                           (TOKEN_MODE ? $"\nOturum (token) geçerlilik: {TOKEN_END_DATE:dd.MM HH:mm}" : "");

                case "duraklat":
                case "durdur":
                    PAUSED = true;
                    LogStatus("Telegram: arama duraklatıldı", null, true);
                    return "⏸ Arama duraklatıldı. Devam için /devam";

                case "devam":
                    PAUSED = false;
                    LogStatus("Telegram: aramaya devam", null, true);
                    return "▶️ Arama devam ediyor.";

                case "tarih":
                    if (args.Length == 0)
                        return $"Tarih: {DateRangeText()}\nDeğiştirmek için: /tarih GG-AA-YYYY [GG-AA-YYYY]";
                    if (!TryParseDateRangeArgs(args, DateTime.Today, out var s, out var e, out var err))
                        return "❌ " + err;
                    SetDateRange(s, e);
                    LogStatus($"Telegram: tarih aralığı değişti -> {s:dd.MM.yyyy} - {e:dd.MM.yyyy}", null, true);
                    return $"📅 Yeni tarih aralığı: {s:dd.MM.yyyy} - {e:dd.MM.yyyy}\nSadece bu aralıktaki randevular alınır; bitince bot durur.";

                case "token":
                    if (!TOKEN_MODE)
                        return "Bu komut yalnızca MHRS_AUTH_MODE=token iken kullanılır.";
                    if (args.Length != 1)
                        return "Kullanım: /token <U2FsdGVkX1... veya eyJ... değeri>";
                    if (!MhrsTokenUtil.TryExtractJwt(args[0], DateTime.Now, out var jwt, out var terr))
                        return "❌ " + terr;
                    File.WriteAllText(TokenFilePath, jwt);
                    LogStatus("Telegram: yeni oturum (token) alındı", null, true);
                    return $"✅ Oturum alındı, {JwtTokenUtil.GetTokenExpireTime(jwt):dd.MM HH:mm}'e kadar geçerli.\nGüvenlik için bu sohbetteki /token mesajınızı silin.";

                case "hedef":
                    var t = CURRENT_TARGET;
                    return t == null
                        ? "Hedef henüz belirlenmedi."
                        : $"🎯 İl {t.Province}, İlçe {t.District}, Klinik {t.Clinic}, Hastane {t.Hospital}, Yer {t.Place}, Doktor {t.DoctorsText}\n(-1 = farketmez)";

                default:
                    return HELP_TEXT;
            }
        }

        /// <summary>Her deneme sonunda canlı durumu günceller (/durum için).</summary>
        static void RecordAttempt(int attempt, SlotSearchResult? result)
        {
            STATE_ATTEMPTS = attempt;
            STATE_LAST_ATTEMPT = DateTime.Now;
            if (result == null)
                STATE_LAST_RESULT = "hata";
            else if (result.IsError)
            {
                STATE_ERRORS_TOTAL++;
                STATE_LAST_RESULT = $"hata ({result.ErrorCode})";
            }
            else
                STATE_LAST_RESULT = result.Slot != null ? $"slot bulundu {result.Slot.BaslangicZamani}" : "randevu yok";
        }
    }
}
