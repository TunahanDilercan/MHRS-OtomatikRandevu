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

            // .env dosyasını elle yükle (DotNetEnv'e gerek kalmadan)
            if (File.Exists(".env"))
            {
                foreach (var line in File.ReadAllLines(".env"))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                        Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
                }
            }

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

            // ENV DEĞERLERİNİ OKU
            TC_NO = Environment.GetEnvironmentVariable("MHRS_TC") ?? string.Empty;
            SIFRE = Environment.GetEnvironmentVariable("MHRS_PASSWORD") ?? string.Empty;
            
            // Tarih ve bildirim ayarlarını oku
            var envStartDateRaw = Environment.GetEnvironmentVariable("MHRS_START_DATE");
            try
            {
                if (!string.IsNullOrEmpty(envStartDateRaw))
                {
                    var dateArr = envStartDateRaw.Split('-').Select(x => Convert.ToInt32(x)).ToArray();
                    var date = new DateTime(dateArr[2], dateArr[1], dateArr[0]);
                    ENV_START_DATE = date.ToString("dd-MM-yyyy");
                }
                else
                {
                    ENV_START_DATE = DateTime.Now.ToString("dd-MM-yyyy");
                }
            }
            catch
            {
                ENV_START_DATE = DateTime.Now.ToString("dd-MM-yyyy");
            }

            var telegramFreq = Environment.GetEnvironmentVariable("TELEGRAM_NOTIFY_FREQUENCY");
            if (!string.IsNullOrEmpty(telegramFreq) && int.TryParse(telegramFreq, out int freq))
                TELEGRAM_NOTIFY_FREQUENCY = freq;

            CONTINUOUS = Environment.GetEnvironmentVariable("MHRS_CONTINUOUS")?.Trim() == "1";
            var intervalRaw = Environment.GetEnvironmentVariable("MHRS_INTERVAL_SECONDS");
            if (!string.IsNullOrWhiteSpace(intervalRaw))
            {
                if (int.TryParse(intervalRaw.Trim(), out int iv) && iv >= 30)
                    INTERVAL_SECONDS = iv;
                else
                    Console.WriteLine($"[UYARI] MHRS_INTERVAL_SECONDS geçersiz veya 30'dan küçük ('{intervalRaw}'), {INTERVAL_SECONDS} sn kullanılıyor.");
            }

            // Zamanlama ayarlarını oku
            var scheduleTimes = Environment.GetEnvironmentVariable("MHRS_SCHEDULE_TIMES");
            if (!string.IsNullOrEmpty(scheduleTimes))
            {
                ENV_SCHEDULE_TIMES = scheduleTimes.Split(',')
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
                Console.WriteLine($"[INFO] Özel zamanlama aktif: {string.Join(", ", ENV_SCHEDULE_TIMES)}");
            }

            // Sunucu ortamında interaktif giriş yapılmasın, eksikse hata verip çık
            if (string.IsNullOrEmpty(TC_NO) || string.IsNullOrEmpty(SIFRE))
            {
                Console.WriteLine("[ERROR] MHRS_TC veya MHRS_PASSWORD çevre değişkenleri bulunamadı. Lütfen .env dosyasını kontrol edin.");
                return;
            }
            else
            {
                Console.WriteLine($"[INFO] TC ve şifre çevre değişkenlerinden yüklendi.");
            }

            // ÖNCE LOGIN YAP VE JWT TOKEN AL
            Console.WriteLine("[INFO] MHRS sistemine giriş yapılıyor...");
            var loginResult = GetToken(_client!);
            if (loginResult == null || string.IsNullOrEmpty(loginResult.Token))
            {
                Console.WriteLine("[ERROR] MHRS sistemine giriş yapılamadı! TC kimlik ve şifrenizi kontrol edin.");
                ExitForLoginFailure("ilk giriş");
                return;
            }
            JWT_TOKEN = loginResult.Token;
            TOKEN_END_DATE = loginResult.Expiration;
            _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
            Console.WriteLine("[INFO] MHRS sistemine başarıyla giriş yapıldı.");

            // İl Seçim Bölümü
            var provinceIdStr = Environment.GetEnvironmentVariable("MHRS_PROVINCE_ID");
            int provinceIndex = !string.IsNullOrEmpty(provinceIdStr) ? int.Parse(provinceIdStr) : -1;
            
            List<GenericResponseModel>? provinceListResponse = null;
            try
            {
                provinceListResponse = _client.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, MHRSUrls.GetProvinces);
            }
            catch (SessionExpiredException ex)
            {
                Console.WriteLine($"[WARNING] 🔄 Session expired during province list retrieval: {ex.Message}");
                LogStatus("Session expired during province list retrieval, attempting recovery", null, true);
                
                // Attempt session recovery
                var newToken = ForceLogin(_client!);
                if (newToken == null || string.IsNullOrEmpty(newToken.Token))
                {
                    Console.WriteLine("[ERROR] ❌ Session recovery failed during initial setup! Bot will exit.");
                    LogStatus("Session recovery failed during initial setup! Bot exiting.", null, true);
                    return;
                }
                
                // Update token
                JWT_TOKEN = newToken.Token;
                TOKEN_END_DATE = newToken.Expiration;
                _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
                
                Console.WriteLine("[INFO] ✅ Session recovery completed. Retrying province list retrieval...");
                LogStatus("Session recovery completed, retrying province list retrieval", null, true);
                
                // Retry the province list request with new token
                try
                {
                    provinceListResponse = _client.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, MHRSUrls.GetProvinces);
                }
                catch (SessionExpiredException retryEx)
                {
                    Console.WriteLine($"[ERROR] ❌ Session expired again after recovery: {retryEx.Message}");
                    Console.WriteLine("[ERROR] This suggests multiple logins or system issues. Please check:");
                    Console.WriteLine("  1. Make sure no other instances of the bot are running");
                    Console.WriteLine("  2. Make sure you're not logged in from another browser/device");
                    Console.WriteLine("  3. Try running the bot later if MHRS system is under maintenance");
                    LogStatus("Session expired again after recovery during initial setup", null, true);
                    return;
                }
            }
            
            if (provinceListResponse == null || !provinceListResponse.Any())
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return;
            }
            var provinceList = provinceListResponse
                                    .DistinctBy(x => x.ValueAsInt)
                                    .OrderBy(x => x.ValueAsInt)
                                    .ToList();
            var istanbulSubLocationIds = new int[] { 341, 342 };
            
            // Server mode: Validate Province ID directly
            bool isValidProvince = provinceList.Any(x => x.ValueAsInt == provinceIndex) || istanbulSubLocationIds.Contains(provinceIndex);
            
            if (!isValidProvince)
            {
                Console.WriteLine($"[ERROR] MHRS_PROVINCE_ID ({provinceIndex}) geçersiz. Lütfen .env dosyasını ve plaka kodunu kontrol edin.");
                Console.WriteLine("İpucu: İstanbul Avrupa için 341, Anadolu için 342, diğer iller için plaka kodu kullanın.");
                return;
            }

            // İlçe Seçim Bölümü
            var districtIdStr = Environment.GetEnvironmentVariable("MHRS_DISTRICT_ID");
            int districtIndex = -2; // -2: hiç seçilmedi, -1: FARKETMEZ
            
            List<GenericResponseModel>? districtList = null;
            try
            {
                districtList = _client.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDistricts, provinceIndex));
            }
            catch (SessionExpiredException ex)
            {
                Console.WriteLine($"[WARNING] 🔄 Session expired during district list retrieval: {ex.Message}");
                LogStatus("Session expired during district list retrieval, attempting recovery", null, true);
                
                // Attempt session recovery
                var newToken = ForceLogin(_client!);
                if (newToken == null || string.IsNullOrEmpty(newToken.Token))
                {
                    Console.WriteLine("[ERROR] ❌ Session recovery failed during district setup! Bot will exit.");
                    LogStatus("Session recovery failed during district setup! Bot exiting.", null, true);
                    return;
                }
                
                // Update token
                JWT_TOKEN = newToken.Token;
                TOKEN_END_DATE = newToken.Expiration;
                _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
                
                Console.WriteLine("[INFO] ✅ Session recovery completed. Retrying district list retrieval...");
                LogStatus("Session recovery completed, retrying district list retrieval", null, true);
                
                // Retry the district list request with new token
                try
                {
                    districtList = _client.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDistricts, provinceIndex));
                }
                catch (SessionExpiredException retryEx)
                {
                    Console.WriteLine($"[ERROR] ❌ Session expired again after recovery: {retryEx.Message}");
                    LogStatus("Session expired again after recovery during district setup", null, true);
                    return;
                }
            }
            
            if (districtList == null || !districtList.Any())
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return;
            }
            if (!string.IsNullOrEmpty(districtIdStr))
            {
                int envDistrictId = int.Parse(districtIdStr);
                if (envDistrictId == -1 || envDistrictId == 0)
                {
                    districtIndex = -1;
                }
                else
                {
                    var found = districtList.FirstOrDefault(x => x.ValueAsInt == envDistrictId);
                    if (found != null)
                        districtIndex = found.ValueAsInt;
                    else
                        districtIndex = -2; // ID bulunamazsa kullanıcıdan istenir
                }
            }
            // Server mode: Validate District ID
            if (districtIndex < -1)
            {
                Console.WriteLine("[ERROR] MHRS_DISTRICT_ID ayarlanmamış veya geçersiz. İsterseniz -1 (Farketmez) yapabilirsiniz.");
                return;
            }

            // Klinik Seçim Bölümü
            var clinicIdStr = Environment.GetEnvironmentVariable("MHRS_CLINIC_ID");
            int clinicIndex = -1;
            var clinicListResponse = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetClinics, provinceIndex, districtIndex));
            if (!clinicListResponse.Success && (clinicListResponse.Data == null || !clinicListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return;
            }
            var clinicList = clinicListResponse.Data;
            if (!string.IsNullOrEmpty(clinicIdStr))
            {
                int envClinicId = int.Parse(clinicIdStr);
                var found = clinicList.FirstOrDefault(x => x.ValueAsInt == envClinicId);
                if (found != null)
                    clinicIndex = found.ValueAsInt;
            }
            // Server mode: Validate Clinic ID
            if (clinicIndex < 0)
            {
                Console.WriteLine("[ERROR] MHRS_CLINIC_ID ayarlanmamış veya geçersiz. Bir klinik seçmelisiniz.");
                Console.WriteLine("Mevcut klinikler:");
                foreach(var c in clinicList) Console.WriteLine($"  ID: {c.ValueAsInt} - {c.Text}");
                return;
            }

            // Hastane Seçim Bölümü
            var hospitalIdStr = Environment.GetEnvironmentVariable("MHRS_HOSPITAL_ID");
            int hospitalIndex = -1;
            var hospitalListResponse = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetHospitals, provinceIndex, districtIndex, clinicIndex));
            if (!hospitalListResponse.Success && (hospitalListResponse.Data == null || !hospitalListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return;
            }
            var hospitalList = hospitalListResponse.Data;
            if (!string.IsNullOrEmpty(hospitalIdStr))
            {
                int envHospitalId = int.Parse(hospitalIdStr);
                var found = hospitalList.FirstOrDefault(x => x.ValueAsInt == envHospitalId);
                if (found != null)
                    hospitalIndex = found.ValueAsInt;
                else if (envHospitalId != -1 && envHospitalId != 0)
                {
                    // Yanlış ID'de sessizce FARKETMEZ'e düşmek yerine dur
                    Console.WriteLine($"[ERROR] MHRS_HOSPITAL_ID={envHospitalId} listede yok. Geçerli seçenekler:");
                    Console.WriteLine("  -1 - FARKETMEZ");
                    foreach (var x in hospitalList) Console.WriteLine($"  {x.ValueAsInt} - {x.Text}");
                    return;
                }
            }
            // Server mode: Hospital validation
            if (hospitalIndex < -1)
            {
                Console.WriteLine("[ERROR] MHRS_HOSPITAL_ID (-1 veya geçerli ID) olmalıdır.");
                return;
            }

            // Muayene Yeri Seçim Bölümü
            var placeIdStr = Environment.GetEnvironmentVariable("MHRS_PLACE_ID");
            int placeIndex = -1;
            var placeListResponse = _client.Get<List<ClinicResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetPlaces, hospitalIndex, clinicIndex));
            if (!placeListResponse.Success && (placeListResponse.Data == null || !placeListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return;
            }
            var placeList = placeListResponse.Data;
            if (!string.IsNullOrEmpty(placeIdStr))
            {
                int envPlaceId = int.Parse(placeIdStr);
                var found = placeList.FirstOrDefault(x => x.ValueAsInt == envPlaceId);
                if (found != null)
                    placeIndex = found.ValueAsInt;
                else if (envPlaceId != -1 && envPlaceId != 0)
                {
                    // Yanlış ID'de sessizce FARKETMEZ'e düşmek yerine dur
                    Console.WriteLine($"[ERROR] MHRS_PLACE_ID={envPlaceId} listede yok. Geçerli seçenekler:");
                    Console.WriteLine("  -1 - FARKETMEZ");
                    foreach (var x in placeList) Console.WriteLine($"  {x.ValueAsInt} - {x.Text}");
                    return;
                }
            }
            // Server mode: Place validation
            if (placeIndex < -1)
            {
                Console.WriteLine("[ERROR] MHRS_PLACE_ID (-1 veya geçerli ID) olmalıdır.");
                return;
            }

            // Doktor Seçim Bölümü
            var doctorIdStr = Environment.GetEnvironmentVariable("MHRS_DOCTOR_ID");
            int doctorIndex = -1;
            var doctorListResponse = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDoctors, hospitalIndex, clinicIndex));
            if (!doctorListResponse.Success && (doctorListResponse.Data == null || !doctorListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return;
            }
            var doctorList = doctorListResponse.Data;
            if (!string.IsNullOrEmpty(doctorIdStr))
            {
                int envDoctorId = int.Parse(doctorIdStr);
                var found = doctorList.FirstOrDefault(x => x.ValueAsInt == envDoctorId);
                if (found != null)
                    doctorIndex = found.ValueAsInt;
                else if (envDoctorId != -1 && envDoctorId != 0)
                {
                    // Yanlış ID'de sessizce FARKETMEZ'e düşmek yerine dur
                    Console.WriteLine($"[ERROR] MHRS_DOCTOR_ID={envDoctorId} listede yok. Geçerli seçenekler:");
                    Console.WriteLine("  -1 - FARKETMEZ");
                    foreach (var x in doctorList) Console.WriteLine($"  {x.ValueAsInt} - {x.Text}");
                    return;
                }
            }
            // Server mode: Doctor validation
            if (doctorIndex < -1)
            {
                Console.WriteLine("[ERROR] MHRS_DOCTOR_ID (-1 veya geçerli ID) olmalıdır.");
                return;
            }

            // Tarih Seçim Bölümü (GG-AA-YYYY). Geçersiz tarih sessizce varsayılana düşmez, bot durur.
            string? startDate;
            string? endDate;

            var envStartDate = Environment.GetEnvironmentVariable("MHRS_START_DATE");
            DateTime startDt = DateTime.Today;
            if (!string.IsNullOrWhiteSpace(envStartDate) && !TryParseTrDate(envStartDate, out startDt))
            {
                ExitWith(2, $"[ERROR] MHRS_START_DATE geçersiz: '{envStartDate}' (biçim: GG-AA-YYYY)", notify: false);
                return;
            }
            startDate = startDt.ToString("yyyy-MM-dd HH:mm:ss");

            var envEndDate = Environment.GetEnvironmentVariable("MHRS_END_DATE");
            DateTime endDt = DateTime.Now.AddDays(12);   // bitiş verilmezse: bugünden 12 gün
            bool hasEndDate = !string.IsNullOrWhiteSpace(envEndDate);
            if (hasEndDate)
            {
                if (!TryParseTrDate(envEndDate!, out var endDay))
                {
                    ExitWith(2, $"[ERROR] MHRS_END_DATE geçersiz: '{envEndDate}' (biçim: GG-AA-YYYY)", notify: false);
                    return;
                }
                endDt = endDay.Date.AddDays(1).AddSeconds(-1);   // bitiş günü dahil (23:59:59)
                if (endDt < startDt)
                {
                    ExitWith(2, $"[ERROR] MHRS_END_DATE ({envEndDate}) başlangıç tarihinden ({envStartDate}) önce.", notify: false);
                    return;
                }
            }
            endDate = endDt.ToString("yyyy-MM-dd HH:mm:ss");
            SLOT_MIN = startDt;
            SLOT_MAX = hasEndDate ? endDt : DateTime.MaxValue;

            #region Randevu Alım Bölümü
            ConsoleUtil.WriteText("Yapmış olduğunuz seçimler doğrultusunda müsait olan ilk randevu otomatik olarak alınacaktır.", 3000);
            Console.Clear();
            
            Console.WriteLine("=== MHRS Otomatik Randevu Botu ===");
            Console.WriteLine($"Başlangıç Zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Arama Kriterleri: İl({provinceIndex}) İlçe({districtIndex}) Klinik({clinicIndex})");
            Console.WriteLine($"Hastane({hospitalIndex}) Yer({placeIndex}) Doktor({doctorIndex})");
            Console.WriteLine($"Tarih Aralığı: {ENV_START_DATE} - {endDate}");
            Console.WriteLine($"Telegram Bildirimi: Her {TELEGRAM_NOTIFY_FREQUENCY} denemede bir");
            Console.WriteLine(CONTINUOUS
                ? $"Arama Modu: Sürekli (her {INTERVAL_SECONDS} sn)"
                : $"Arama Modu: Saat pencereleri (pencere içinde her {INTERVAL_SECONDS} sn)");
            if (SLOT_MAX != DateTime.MaxValue)
                Console.WriteLine($"Bot {SLOT_MAX:dd.MM.yyyy HH:mm} sonrasında kendini durduracak.");
            Console.WriteLine("=====================================");
            Console.WriteLine("Bot çalışıyor... (Sadece önemli olaylar gösterilecek)");
            Console.WriteLine();

            LogStatus($"Bot başlatıldı - Kriterler: İl({provinceIndex}) İlçe({districtIndex}) Klinik({clinicIndex}) Hastane({hospitalIndex}) Yer({placeIndex}) Doktor({doctorIndex})", null, true);

            bool appointmentState = false;
            int attemptCount = 0;
            int consecutiveErrors = 0;   // üst üste arama hatası (randevu yok değil, gerçek hata)
            bool firstTestDone = false;

            // İlk randevu kontrolü (sürekli arama modu) - HEMEN BAŞLA
            Console.WriteLine("🔍 Sürekli randevu arama modu başlatılıyor... (İlk 5 deneme 3 dakikada bir)");
            LogStatus("Sürekli randevu arama modu başlatılıyor - İlk 5 deneme 3 dakikada bir", null, true);

            // İlk başlatma bildirimi gönder
            if (_notificationService != null)
            {
                var startMessage = $"🚀 MHRS Bot Sürekli Arama Başladı!\n\n🕐 Başlangıç: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n🎯 Hedef: İl({provinceIndex}) İlçe({districtIndex}) Klinik({clinicIndex})\n📅 Tarih: {ENV_START_DATE}\n� Sürekli arama: İlk 5 deneme 3 dakikada bir\n⏰ Sonra belirlenen saatlerde çalışır";
                _ = Task.Run(() => _notificationService.SendNotification(startMessage));
            }

            while (!appointmentState)
            {
                if (DateTime.Now > SLOT_MAX)
                {
                    ExitWith(2, $"⌛ MHRS Bot durdu: hedef tarih aralığı geçti ({SLOT_MAX:dd.MM.yyyy}). {attemptCount} denemede randevu bulunamadı.", notify: true);
                    return;
                }

                // İlk başlatmada her zaman çalış, sonra belirlenen saatlerde çalış
                if (!firstTestDone || attemptCount <= 5) // İlk 5 deneme sürekli (3 dakikada bir = 15 dakika)
                {
                    if (!firstTestDone)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 İlk randevu kontrolü - Sürekli arama modu başlıyor");
                        LogStatus("İlk randevu kontrolü - Sürekli arama modu başlıyor", null, true);
                        firstTestDone = true;
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 Deneme #{attemptCount} - Sürekli arama devam ediyor (3 dakikada bir)");
                        LogStatus($"Deneme #{attemptCount} - Sürekli arama devam ediyor (3 dakikada bir)", null, true);
                    }
                }
                else
                {
                    // 5. denemeden sonra belirlenen saat aralıklarında çalış
                    if (!CONTINUOUS && !IsWithinAllowedWindow(DateTime.Now))
                    {
                        if (attemptCount % 30 == 0) // Her 30 denemede bir konsola göster
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Saat aralığı dışında, bekleniyor... (Deneme #{attemptCount})");
                        }
                        LogStatus("Saat aralığı dışında, bekleniyor");
                        Thread.Sleep(TimeSpan.FromSeconds(30));
                        continue;
                    }
                }

                if (TOKEN_END_DATE == default || TOKEN_END_DATE < DateTime.Now)
                {
                    var tkn = GetToken(_client!);
                    if (tkn == null || string.IsNullOrEmpty(tkn.Token))
                    {
                        LogStatus("Yeniden giriş hatası", null, true);
                        ExitForLoginFailure("token yenileme");
                        return;
                    }
                    JWT_TOKEN = tkn.Token;
                    TOKEN_END_DATE = tkn.Expiration;
                    _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
                    LogStatus("Token yenilendi", null, true);
                }

                attemptCount++;
                var slotRequestModel = new SlotRequestModel
                {
                    MhrsHekimId = doctorIndex,
                    MhrsIlId = provinceIndex,
                    MhrsIlceId = districtIndex,
                    MhrsKlinikId = clinicIndex,
                    MhrsKurumId = hospitalIndex,
                    MuayeneYeriId = placeIndex,
                    BaslangicZamani = startDate,
                    BitisZamani = endDate
                };

                SubSlot? slot = null;
                SlotSearchResult? search = null;
                try
                {
                    search = GetSlot(_client!, slotRequestModel);
                    slot = search.Slot;
                }
                catch (SessionExpiredException ex)
                {
                    Console.WriteLine($"[WARNING] 🔄 Session expired detected: {ex.Message}");
                    LogStatus($"Session expired detected, attempting recovery (Deneme #{attemptCount})", null, true);
                    
                    // Attempt session recovery
                    var newToken = ForceLogin(_client!);
                    if (newToken == null || string.IsNullOrEmpty(newToken.Token))
                    {
                        Console.WriteLine("[ERROR] ❌ Session recovery failed! Bot will exit.");
                        LogStatus("Session recovery failed! Bot exiting.", null, true);
                        ExitForLoginFailure($"oturum kurtarma (deneme #{attemptCount})");
                        return;
                    }
                    
                    // Update token
                    JWT_TOKEN = newToken.Token;
                    TOKEN_END_DATE = newToken.Expiration;
                    _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
                    
                    Console.WriteLine("[INFO] ✅ Session recovery completed. Retrying slot request...");
                    LogStatus($"Session recovery completed, retrying slot request (Deneme #{attemptCount})", null, true);
                    
                    // Send recovery success notification
                    if (_notificationService != null)
                    {
                        var recoveryMessage = $"✅ Session Recovery Başarılı!\n\n🔄 MHRS Bot otomatik olarak session'ı yeniledi\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n🔄 Deneme: #{attemptCount}\n🎯 Randevu arama devam ediyor...";
                        _ = Task.Run(() => _notificationService.SendNotification(recoveryMessage));
                    }
                    
                    // Retry the slot request with new token
                    try
                    {
                        search = GetSlot(_client!, slotRequestModel);
                        slot = search.Slot;
                    }
                    catch (SessionExpiredException retryEx)
                    {
                        Console.WriteLine($"[ERROR] ❌ Session expired again after recovery: {retryEx.Message}");
                        LogStatus($"Session expired again after recovery (Deneme #{attemptCount})", null, true);
                        
                        if (_notificationService != null)
                        {
                            var retryErrorMessage = $"❌ MHRS Bot Problem!\n\n🔐 Session recovery sonrası tekrar session süresi doldu\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n🔄 Deneme: #{attemptCount}\n\n⚠️ Çoklu giriş veya sistem problemi olabilir";
                            _ = Task.Run(() => _notificationService.SendNotification(retryErrorMessage));
                        }
                        
                        // Skip this iteration, will try again in next loop
                        Thread.Sleep(TimeSpan.FromMinutes(1));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] ❌ Unexpected error during slot request: {ex.Message}");
                    LogStatus($"Unexpected error during slot request: {ex.Message} (Deneme #{attemptCount})", null, true);
                    
                    // Skip this iteration
                    Thread.Sleep(TimeSpan.FromMinutes(1));
                    continue;
                }
                // Gerçek hata ile "randevu yok" ayrımı: hatalar artık sessizce "bulunamadı" sayılmıyor
                if (search != null && search.IsError)
                {
                    consecutiveErrors++;
                    LogStatus($"Arama hatası #{consecutiveErrors}: {search.ErrorCode} {search.ErrorMessage} (Deneme #{attemptCount})", null, true);
                    if ((consecutiveErrors == 5 || consecutiveErrors % 30 == 0) && _notificationService != null)
                    {
                        var errMsg = $"⚠️ MHRS arama hatası ({consecutiveErrors} kez üst üste)\n\nKod: {search.ErrorCode ?? "-"}\nMesaj: {search.ErrorMessage ?? "-"}\n⏰ {DateTime.Now:HH:mm:ss}\n\nBot denemeye devam ediyor; bu süre boyunca randevu bulunamayabilir.";
                        _ = Task.Run(() => _notificationService.SendNotification(errMsg));
                    }
                }
                else if (search != null)
                {
                    if (consecutiveErrors >= 5 && _notificationService != null)
                    {
                        var okMsg = $"✅ MHRS arama tekrar çalışıyor ({consecutiveErrors} hatadan sonra).";
                        _ = Task.Run(() => _notificationService.SendNotification(okMsg));
                    }
                    consecutiveErrors = 0;
                }

                if (slot == null || slot == default)
                {
                    // İlk birkaç deneme ise özel mesaj
                    if (attemptCount <= 5)
                    {
                        if (attemptCount == 1)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Bot çalışıyor ve MHRS'ye bağlandı!");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] � İlk randevu kontrolünde slot bulunamadı, devam ediyor...");
                            LogStatus("Bot çalışıyor - İlk randevu kontrolünde slot bulunamadı, devam ediyor", null, true);
                            
                            // İlk kontrol bildirimi gönder
                            if (_notificationService != null)
                            {
                                var firstCheckMessage = $"✅ Bot Bağlantı Başarılı!\n\n🤖 MHRS'ye başarıyla bağlandı\n🕐 İlk kontrol: {DateTime.Now:HH:mm:ss}\n❌ İlk kontrolde randevu bulunamadı\n🔍 Anında arama devam ediyor...\n📅 Hedef tarih: {ENV_START_DATE}";
                                _ = Task.Run(() => _notificationService.SendNotification(firstCheckMessage));
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 🔍 Deneme #{attemptCount} - Randevu bulunamadı, 3 dakika sonra tekrar");
                            LogStatus($"Deneme #{attemptCount} - Randevu bulunamadı, 3 dakika sonra tekrar", null, true);
                        }
                        
                        // İlk 5 denemede 3 dakika bekle, her denemeyi bildir
                        if (attemptCount > 1 && _notificationService != null)
                        {
                            var searchMessage = $"🔍 Randevu Arama #{attemptCount}\n\n❌ Müsait randevu bulunamadı\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n⏳ 3 dakika sonra tekrar aranacak\n📅 Hedef tarih: {ENV_START_DATE}";
                            _ = Task.Run(() => _notificationService.SendNotification(searchMessage));
                        }
                        
                        // 5. deneme sonrası özet bildirim gönder
                        if (attemptCount == 5 && _notificationService != null)
                        {
                            var summaryMessage = $"📋 İlk 5 Deneme Özeti\n\n🔍 İlk 5 deneme tamamlandı\n⏰ Başlangıç: {DateTime.Now.AddMinutes(-15):HH:mm}\n⏰ Bitiş: {DateTime.Now:HH:mm}\n❌ 5 denemede de randevu bulunamadı\n\n🕐 Bundan sonra belirlenen saat aralıklarında arama yapılacak\n📅 Hedef tarih: {ENV_START_DATE}";
                            _ = Task.Run(() => _notificationService.SendNotification(summaryMessage));
                        }
                        
                        Thread.Sleep(TimeSpan.FromMinutes(3));
                    }
                    else
                    {
                        // Normal deneme mesajları
                        // Basit log kaydı - sadece dosyaya
                        LogStatus($"Deneme #{attemptCount} - Müsait randevu bulunamadı");
                        
                        // Her 10 denemede bir konsola minimal bilgi ver
                        if (attemptCount % 10 == 0)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Deneme #{attemptCount} - Müsait randevu bulunamadı");
                        }
                        
                        // Normal bekleme süresi (MHRS_INTERVAL_SECONDS, varsayılan 120 sn)
                        Thread.Sleep(TimeSpan.FromSeconds(INTERVAL_SECONDS));
                    }
                    
                    // Telegram bildirim frekansına göre "randevu bulunamadı" bildirimi gönder (normal modda - 5. denemeden sonra)
                    if (attemptCount > 5 && attemptCount % TELEGRAM_NOTIFY_FREQUENCY == 0 && _notificationService != null)
                    {
                        var notFoundMessage = $"🔍 Randevu Arama Raporu\n\n❌ {attemptCount} deneme yapıldı, müsait randevu bulunamadı\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n🔄 Arama devam ediyor...\n📅 Hedef tarih: {ENV_START_DATE}";
                        _ = Task.Run(() => _notificationService.SendNotification(notFoundMessage));
                    }
                    
                    // Her 50 denemede bir genel durum raporu gönder
                    if (attemptCount > 1 && attemptCount % 50 == 0 && _notificationService != null)
                    {
                        var statusMessage = $"📊 MHRS Bot Durum Raporu\n\n🔄 Toplam Deneme: {attemptCount}\n⏰ Çalışma Süresi: {DateTime.Now.Subtract(DateTime.Now.Date):hh\\:mm}\n🔍 Durum: Randevu aranıyor...\n📅 Hedef Tarih: {ENV_START_DATE}";
                        _ = Task.Run(() => _notificationService.SendNotification(statusMessage));
                    }
                    
                    // Bekleme yukarıdaki dallarda yapıldı (ilk 5 deneme: 3 dk, sonrası: INTERVAL_SECONDS)
                    continue;
                }

                var appointmentRequestModel = new AppointmentRequestModel
                {
                    FkSlotId = slot.Id,
                    FkCetvelId = slot.FkCetvelId,
                    MuayeneYeriId = slot.MuayeneYeriId,
                    BaslangicZamani = slot.BaslangicZamani,
                    BitisZamani = slot.BitisZamani
                };

                LogStatus($"RANDEVU BULUNDU! - Deneme #{attemptCount}", slot.BaslangicZamani, true);
                Console.WriteLine($"\n🎉 Randevu bulundu!");
                Console.WriteLine($"📅 Tarih: {slot.BaslangicZamani}");
                Console.WriteLine("⏳ Randevu alınıyor...");
                
                // Randevu bulundu bildirimi gönder
                if (_notificationService != null)
                {
                    var foundMessage = $"🎉 RANDEVU BULUNDU!\n\n📅 Tarih: {slot.BaslangicZamani}\n🔄 Deneme: #{attemptCount}\n⏳ Randevu alınıyor...";
                    _ = Task.Run(() => _notificationService.SendNotification(foundMessage));
                }
                
                bool appointmentResult = false;
                try
                {
                    appointmentResult = MakeAppointment(_client!, appointmentRequestModel, sendNotification: true);
                }
                catch (SessionExpiredException ex)
                {
                    Console.WriteLine($"[WARNING] 🔄 Session expired during appointment booking: {ex.Message}");
                    LogStatus($"Session expired during appointment booking, attempting recovery (Deneme #{attemptCount})", slot.BaslangicZamani, true);
                    
                    // Attempt session recovery
                    var newToken = ForceLogin(_client!);
                    if (newToken == null || string.IsNullOrEmpty(newToken.Token))
                    {
                        Console.WriteLine("[ERROR] ❌ Session recovery failed during appointment booking!");
                        LogStatus("Session recovery failed during appointment booking!", slot.BaslangicZamani, true);
                        
                        if (_notificationService != null)
                        {
                            var errorMessage = $"❌ RANDEVU ALINAMADI!\n\n🔐 Session recovery başarısız (randevu alırken)\n📅 Slot: {slot.BaslangicZamani}\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n🔄 Deneme: #{attemptCount}\n\n⚠️ Slot kaybedildi, arama devam ediyor";
                            _ = Task.Run(() => _notificationService.SendNotification(errorMessage));
                        }
                        
                        // Skip this iteration, continue searching
                        Thread.Sleep(TimeSpan.FromMinutes(1));
                        continue;
                    }
                    
                    // Update token
                    JWT_TOKEN = newToken.Token;
                    TOKEN_END_DATE = newToken.Expiration;
                    _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
                    
                    Console.WriteLine("[INFO] ✅ Session recovery completed. Retrying appointment booking...");
                    LogStatus($"Session recovery completed, retrying appointment booking (Deneme #{attemptCount})", slot.BaslangicZamani, true);
                    
                    // Send recovery notification
                    if (_notificationService != null)
                    {
                        var recoveryMessage = $"✅ Session Recovery (Randevu)!\n\n🔄 MHRS Bot session'ı yeniledi\n📅 Slot: {slot.BaslangicZamani}\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n🔄 Deneme: #{attemptCount}\n⏳ Randevu booking tekrar deneniyor...";
                        _ = Task.Run(() => _notificationService.SendNotification(recoveryMessage));
                    }
                    
                    // Retry appointment booking with new token
                    try
                    {
                        appointmentResult = MakeAppointment(_client!, appointmentRequestModel, sendNotification: true);
                    }
                    catch (SessionExpiredException retryEx)
                    {
                        Console.WriteLine($"[ERROR] ❌ Session expired again during appointment retry: {retryEx.Message}");
                        LogStatus($"Session expired again during appointment retry (Deneme #{attemptCount})", slot.BaslangicZamani, true);
                        
                        if (_notificationService != null)
                        {
                            var retryErrorMessage = $"❌ RANDEVU ALINAMADI!\n\n🔐 Recovery sonrası tekrar session süresi doldu\n📅 Slot: {slot.BaslangicZamani}\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n🔄 Deneme: #{attemptCount}\n\n⚠️ Slot kaybedildi, arama devam ediyor";
                            _ = Task.Run(() => _notificationService.SendNotification(retryErrorMessage));
                        }
                        
                        // Skip this iteration, continue searching
                        Thread.Sleep(TimeSpan.FromMinutes(1));
                        continue;
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"[ERROR] ❌ Unexpected error during appointment retry: {retryEx.Message}");
                        LogStatus($"Unexpected error during appointment retry: {retryEx.Message} (Deneme #{attemptCount})", slot.BaslangicZamani, true);
                        appointmentResult = false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] ❌ Unexpected error during appointment booking: {ex.Message}");
                    LogStatus($"Unexpected error during appointment booking: {ex.Message} (Deneme #{attemptCount})", slot.BaslangicZamani, true);
                    appointmentResult = false;
                }
                
                appointmentState = appointmentResult;
                if (appointmentState)
                {
                    LogStatus($"BAŞARILI! Randevu alındı - Deneme #{attemptCount}", slot.BaslangicZamani, true);
                    Console.WriteLine("\n✅ BAŞARILI! Randevu alındı!");
                    Console.WriteLine($"📅 Tarih: {slot.BaslangicZamani}");
                    Console.WriteLine("🔒 Bot durduruldu.");
                    
                    // Başarı dosyası oluştur
                    var successFile = Path.Combine(Directory.GetCurrentDirectory(), "randevu_basarili.txt");
                    File.WriteAllText(successFile, $"Randevu başarıyla alındı!\nTarih: {slot.BaslangicZamani}\nAlınma Zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nToplam Deneme: {attemptCount}");
                }
                else
                {
                    LogStatus($"Randevu alma başarısız - Deneme #{attemptCount}", slot.BaslangicZamani, true);
                    Console.WriteLine("❌ Randevu alma başarısız. Arama devam ediyor...");
                }
            }
            
            LogStatus("Program sonlandı: Randevu başarıyla alındı.", null, true);
            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("\nBot durdu. Herhangi bir tuşa basarak çıkabilirsiniz...");
                Console.ReadKey();   // sunucuda (systemd) klavye yok; orada ReadKey hata fırlatıp çökertiyordu
            }
            Environment.Exit(0);   // randevu alındıysa tamamen çık
            #endregion

            Console.ReadKey();
        }
    }
}
