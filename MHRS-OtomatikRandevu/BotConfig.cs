namespace MHRS_OtomatikRandevu
{
    /// <summary>
    /// .env / ortam değişkenlerinden okunan ve doğrulanan bot ayarları.
    /// Tüm hatalar tek seferde toplanır; geçersiz bir değer sessizce varsayılana düşmez.
    /// </summary>
    public sealed class BotConfig
    {
        /// <summary>FARKETMEZ anlamındaki ID.</summary>
        public const int Any = -1;

        /// <summary>MHRS_AUTH_MODE=token: şifreyle hiç giriş yapma; tarayıcıdan alınan oturumu (/token) kullan.</summary>
        public bool TokenMode { get; private set; }

        public string Tc { get; private set; } = "";
        public string Password { get; private set; } = "";

        public int ProvinceId { get; private set; }
        /// <summary>null = ayarlanmamış (zorunlu), -1 = FARKETMEZ.</summary>
        public int? DistrictId { get; private set; }
        /// <summary>null = ayarlanmamış (zorunlu).</summary>
        public int? ClinicId { get; private set; }
        public int HospitalId { get; private set; } = Any;
        public int PlaceId { get; private set; } = Any;
        /// <summary>Aranacak doktorlar. [-1] = FARKETMEZ. Birden fazla ise her denemede hepsine bakılır.</summary>
        public List<int> DoctorIds { get; private set; } = new() { Any };

        /// <summary>Aramanın başladığı gün (00:00).</summary>
        public DateTime StartDate { get; private set; }
        /// <summary>Bitiş gününün son saniyesi (dahil). null ise bitiş yok (bugün + 12 gün aranır, bot durmaz).</summary>
        public DateTime? EndDate { get; private set; }

        public bool Continuous { get; private set; }
        public int IntervalSeconds { get; private set; } = 120;
        public int NotifyFrequency { get; private set; } = 10;
        public List<string> ScheduleTimes { get; private set; } = new();
        /// <summary>TELEGRAM_COMMANDS=1: /durum, /duraklat, /devam, /tarih, /hedef komutlarını dinle.</summary>
        public bool TelegramCommands { get; private set; }
        /// <summary>Her sabah 09:00'da durum özeti (TELEGRAM_DAILY_REPORT=0 ile kapatılır).</summary>
        public bool DailyReport { get; private set; } = true;
        /// <summary>MHRS_DRY_RUN=1: her şey gerçek (giriş, arama, bildirim) ama randevu ALINMAZ. Canlı deneme için.</summary>
        public bool DryRun { get; private set; }

        /// <summary>Botu durdurmayan ama kullanıcıya gösterilmesi gereken uyarılar.</summary>
        public List<string> Warnings { get; } = new();

        public static (BotConfig Config, List<string> Errors) FromEnvironment() =>
            From(Environment.GetEnvironmentVariable, DateTime.Today);

        public static (BotConfig Config, List<string> Errors) From(Func<string, string?> get, DateTime today)
        {
            var c = new BotConfig();
            var errors = new List<string>();
            string? Val(string key) => string.IsNullOrWhiteSpace(get(key)) ? null : get(key)!.Trim();

            var authMode = (Val("MHRS_AUTH_MODE") ?? "password").ToLowerInvariant();
            if (authMode != "password" && authMode != "token")
                errors.Add($"MHRS_AUTH_MODE 'password' veya 'token' olmalı: '{authMode}'");
            c.TokenMode = authMode == "token";

            c.Tc = Val("MHRS_TC") ?? "";
            c.Password = Val("MHRS_PASSWORD") ?? "";
            if (!c.TokenMode && (c.Tc == "" || c.Password == ""))
                errors.Add("MHRS_TC veya MHRS_PASSWORD bulunamadı. Lütfen .env dosyasını kontrol edin.");

            // İl zorunlu
            var province = ParseId("MHRS_PROVINCE_ID", Val("MHRS_PROVINCE_ID"), errors);
            if (province == null && Val("MHRS_PROVINCE_ID") == null)
                errors.Add("MHRS_PROVINCE_ID ayarlanmamış (il plaka kodu; İstanbul Avrupa 341, Anadolu 342).");
            c.ProvinceId = province ?? 0;

            c.DistrictId = NormalizeAny(ParseId("MHRS_DISTRICT_ID", Val("MHRS_DISTRICT_ID"), errors));
            c.ClinicId = ParseId("MHRS_CLINIC_ID", Val("MHRS_CLINIC_ID"), errors);
            // Hastane / muayene yeri / doktor boşsa FARKETMEZ
            c.HospitalId = NormalizeAny(ParseId("MHRS_HOSPITAL_ID", Val("MHRS_HOSPITAL_ID"), errors)) ?? Any;
            c.PlaceId = NormalizeAny(ParseId("MHRS_PLACE_ID", Val("MHRS_PLACE_ID"), errors)) ?? Any;
            var doctorsRaw = Val("MHRS_DOCTOR_ID");
            if (doctorsRaw != null)
            {
                var parts = doctorsRaw.Split(',').Select(x => x.Trim()).Where(x => x != "").ToList();
                var ids = parts.Select(x => NormalizeAny(ParseId("MHRS_DOCTOR_ID", x, errors))).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
                if (ids.Count > 1 && ids.Contains(Any))
                    errors.Add("MHRS_DOCTOR_ID: -1 (FARKETMEZ) başka doktor ID'leriyle birlikte kullanılamaz.");
                if (ids.Count > 0)
                    c.DoctorIds = ids;
            }

            // Tarihler (GG-AA-YYYY)
            c.StartDate = today.Date;
            var startRaw = Val("MHRS_START_DATE");
            if (startRaw != null)
            {
                if (Program.TryParseTrDate(startRaw, out var sd)) c.StartDate = sd.Date;
                else errors.Add($"MHRS_START_DATE geçersiz: '{startRaw}' (biçim: GG-AA-YYYY)");
            }
            var endRaw = Val("MHRS_END_DATE");
            if (endRaw != null)
            {
                if (Program.TryParseTrDate(endRaw, out var ed))
                {
                    c.EndDate = ed.Date.AddDays(1).AddSeconds(-1);   // bitiş günü dahil
                    if (c.EndDate < c.StartDate)
                        errors.Add($"MHRS_END_DATE ({endRaw}) başlangıç tarihinden ({c.StartDate:dd-MM-yyyy}) önce.");
                }
                else errors.Add($"MHRS_END_DATE geçersiz: '{endRaw}' (biçim: GG-AA-YYYY)");
            }

            c.Continuous = Val("MHRS_CONTINUOUS") == "1";
            c.TelegramCommands = Val("TELEGRAM_COMMANDS") == "1";
            c.DailyReport = Val("TELEGRAM_DAILY_REPORT") != "0";
            c.DryRun = Val("MHRS_DRY_RUN") == "1";

            var interval = Val("MHRS_INTERVAL_SECONDS");
            if (interval != null)
            {
                if (int.TryParse(interval, out int iv) && iv >= 30) c.IntervalSeconds = iv;
                else c.Warnings.Add($"MHRS_INTERVAL_SECONDS geçersiz veya 30'dan küçük ('{interval}'), {c.IntervalSeconds} sn kullanılıyor.");
            }

            var freq = Val("TELEGRAM_NOTIFY_FREQUENCY");
            if (freq != null)
            {
                // 0 veya negatif değer döngüde sıfıra bölmeye yol açıyordu
                if (int.TryParse(freq, out int f) && f > 0) c.NotifyFrequency = f;
                else c.Warnings.Add($"TELEGRAM_NOTIFY_FREQUENCY geçersiz ('{freq}'), {c.NotifyFrequency} kullanılıyor.");
            }

            var schedule = Val("MHRS_SCHEDULE_TIMES");
            if (schedule != null)
            {
                c.ScheduleTimes = schedule.Split(',').Select(x => x.Trim()).Where(x => x != "").ToList();
                var bad = c.ScheduleTimes.Where(x => !TimeSpan.TryParseExact(x, "hh\\:mm", null, out _)).ToList();
                if (bad.Any())
                    errors.Add($"MHRS_SCHEDULE_TIMES geçersiz saat(ler): {string.Join(", ", bad)} (biçim: SS:DD, virgülle ayrılmış)");
            }

            return (c, errors);
        }

        static int? ParseId(string key, string? raw, List<string> errors)
        {
            if (raw == null) return null;
            if (int.TryParse(raw, out int v)) return v;
            errors.Add($"{key} sayı olmalı: '{raw}'");
            return null;
        }

        /// <summary>MHRS menülerinde 0 ve -1 ikisi de FARKETMEZ anlamında kullanılıyordu.</summary>
        static int? NormalizeAny(int? id) => id == 0 ? Any : id;
    }
}
