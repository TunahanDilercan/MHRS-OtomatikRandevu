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
    public class Program
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
        internal static bool IsWithinAllowedWindow(DateTime t)
        {
            // Eğer özel saat listesi tanımlıysa sadece o saatleri kontrol et
            if (ENV_SCHEDULE_TIMES.Count > 0)
            {
                var currentTime = t.ToString("HH:mm");
                return ENV_SCHEDULE_TIMES.Contains(currentTime);
            }

            var h = t.Hour;  var m = t.Minute;

            bool hourly  = (m >= 57 || m <= 4);
            bool night   = (h == 0 && m >= 1 && m <= 6) ||
                           (h == 1 && m == 59) ||
                           (h == 2 && m <= 3);
            bool morning = (h == 9 && m >= 55) || (h == 10 && m <= 15);
            bool evening = (h == 19 && m >= 55) || (h == 20 && m <= 15);
            
            // 15-45 dakika arası rastgele 3'lü gruplar (her saatte 2 grup = 6 dakika)
            bool midHourRandom = IsInRandomMidHourWindow(h, m);

            return hourly || night || morning || evening || midHourRandom;
        }

        static bool IsInRandomMidHourWindow(int hour, int minute)
        {
            // Her saat için sabit rastgele dakikalar (15-45 arası)
            var randomTimes = GetRandomTimesForHour(hour);
            return randomTimes.Contains(minute);
        }

        static List<int> GetRandomTimesForHour(int hour)
        {
            // Her saat için sabit seed kullanarak tutarlı rastgele dakikalar üret
            var random = new Random(hour * 1000 + DateTime.Today.DayOfYear);
            var times = new List<int>();
            
            // 15-45 dakika arası 2 grup, her grup 3 ardışık dakika
            var availableMinutes = Enumerable.Range(15, 31).ToList(); // 15-45 arası (15-45)
            
            // İlk grup (3 ardışık dakika) - sondan 2 çıkarıp güvenli aralık bırak
            if (availableMinutes.Count >= 3)
            {
                int firstStart = availableMinutes[random.Next(0, availableMinutes.Count - 2)];
                times.AddRange(new[] { firstStart, firstStart + 1, firstStart + 2 });
                
                // İkinci grup için kullanılan dakikaları ve çevresini çıkar
                var removeStart = Math.Max(0, firstStart - 5);
                var removeEnd = Math.Min(availableMinutes.Count - 1, firstStart + 7);
                var removeCount = removeEnd - removeStart + 1;
                
                if (removeStart < availableMinutes.Count && removeCount > 0 && removeCount <= availableMinutes.Count - removeStart)
                {
                    availableMinutes.RemoveRange(removeStart, removeCount);
                }
                
                // İkinci grup
                if (availableMinutes.Count >= 3)
                {
                    int secondStart = availableMinutes[random.Next(0, availableMinutes.Count - 2)];
                    times.AddRange(new[] { secondStart, secondStart + 1, secondStart + 2 });
                }
            }
            
            return times.OrderBy(x => x).ToList();
        }

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

        static JwtTokenModel? GetToken(IClientService client)
        {
            // Cross-platform token file path
            var tokenFilePath = Path.Combine(Directory.GetCurrentDirectory(), TOKEN_FILE_NAME);

            try
            {
                var tokenData = File.ReadAllText(tokenFilePath);
                if (string.IsNullOrEmpty(tokenData) || JwtTokenUtil.GetTokenExpireTime(tokenData) < DateTime.Now)
                    throw new Exception();

                return new() { Token = tokenData, Expiration = JwtTokenUtil.GetTokenExpireTime(tokenData) };
            }
            catch (Exception)
            {
                var loginRequestModel = new LoginRequestModel
                {
                    KullaniciAdi = TC_NO,
                    Parola = SIFRE
                };

                var loginResponse = client.Post<LoginResponseModel>(MHRSUrls.BaseUrl, MHRSUrls.Login, loginRequestModel).Result;
                if (loginResponse.Data == null || string.IsNullOrEmpty(loginResponse.Data?.Jwt))
                {
                    LAST_LOGIN_ERROR = ApiErrorUtil.First(loginResponse.Errors);
                    Console.WriteLine("Giriş yapılırken bir hata meydana geldi!");
                    return null;
                }

                if (!string.IsNullOrEmpty(tokenFilePath))
                    File.WriteAllText(tokenFilePath, loginResponse.Data!.Jwt);

                return new() { Token = loginResponse.Data!.Jwt, Expiration = JwtTokenUtil.GetTokenExpireTime(loginResponse.Data!.Jwt) };
            }
        }

        /// <summary>
        /// Forces a new login by clearing the cached token and re-authenticating
        /// </summary>
        static JwtTokenModel? ForceLogin(IClientService client)
        {
            Console.WriteLine("[INFO] 🔄 Session recovery: Forcing fresh login...");
            LogStatus("Session recovery: Forcing fresh login", null, true);
            
            // Clear cached token file to force fresh login (cross-platform path)
            var tokenFilePath = Path.Combine(Directory.GetCurrentDirectory(), TOKEN_FILE_NAME);
            
            // Delete existing token file
            try
            {
                if (File.Exists(tokenFilePath))
                {
                    File.Delete(tokenFilePath);
                    Console.WriteLine("[INFO] 🗑️  Cleared cached token");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Could not delete token file: {ex.Message}");
            }
            
            // Clear authorization header to avoid sending expired token
            client.ClearAuthorizationHeader();
            Console.WriteLine("[INFO] 🔓 Cleared authorization header");
            
            // Perform fresh login
            var loginRequestModel = new LoginRequestModel
            {
                KullaniciAdi = TC_NO,
                Parola = SIFRE
            };

            try
            {
                var loginResponse = client.Post<LoginResponseModel>(MHRSUrls.BaseUrl, MHRSUrls.Login, loginRequestModel).Result;
                if (loginResponse.Data == null || string.IsNullOrEmpty(loginResponse.Data?.Jwt))
                {
                    LAST_LOGIN_ERROR = ApiErrorUtil.First(loginResponse.Errors);
                    Console.WriteLine("[ERROR] ❌ Session recovery failed: Could not obtain new token");
                    LogStatus("Session recovery failed: Could not obtain new token", null, true);
                    return null;
                }

                // Save new token
                if (!string.IsNullOrEmpty(tokenFilePath))
                    File.WriteAllText(tokenFilePath, loginResponse.Data!.Jwt);

                Console.WriteLine("[INFO] ✅ Session recovery successful: New token obtained");
                LogStatus("Session recovery successful: New token obtained", null, true);
                
                return new() { Token = loginResponse.Data!.Jwt, Expiration = JwtTokenUtil.GetTokenExpireTime(loginResponse.Data!.Jwt) };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] ❌ Session recovery failed with exception: {ex.Message}");
                LogStatus($"Session recovery failed with exception: {ex.Message}", null, true);
                return null;
            }
        }

        //Aynı gün içerisinde tek slot mevcut ise o slotu bulur
        //Aynı gün içerisinde birden fazla slot mevcut ise en yakın saati getirmez fakat en yakın güne ait bir slot getirir
        record SlotSearchResult(SubSlot? Slot, bool IsError = false, string? ErrorCode = null, string? ErrorMessage = null);

        static SlotSearchResult GetSlot(IClientService client, SlotRequestModel slotRequestModel)
        {
            var slotListResponse = client.Post<List<SlotResponseModel>>(MHRSUrls.BaseUrl, MHRSUrls.GetSlots, slotRequestModel).Result;
            if (slotListResponse.Data is null)
            {
                // RND4010 = "randevu bulunamadı": normal durum
                if (ApiErrorUtil.Contains(slotListResponse.Errors, "RND4010"))
                    return new SlotSearchResult(null);

                // Diğer her şey gerçek hata (bağlantı, bakım, IP engeli, yetki...)
                var (kod, mesaj) = ApiErrorUtil.First(slotListResponse.Errors);
                return new SlotSearchResult(null, true, kod ?? "BAGLANTI", mesaj ?? "MHRS'den yanıt alınamadı");
            }

            var slot = SelectEarliestSlot(slotListResponse.Data, SLOT_MIN, SLOT_MAX, DateTime.Now);
            return new SlotSearchResult(slot);
        }

        /// <summary>
        /// Tüm hekim / muayene yeri / gün kombinasyonlarındaki boş slotlardan, [min, max] aralığında
        /// olan ve henüz geçmemiş EN ERKEN slotu seçer. Yan etkisi yoktur (test edilebilir).
        /// </summary>
        internal static SubSlot? SelectEarliestSlot(IEnumerable<SlotResponseModel>? data, DateTime min, DateTime max, DateTime now)
        {
            if (data == null)
                return null;

            return data
                .Where(r => r?.HekimSlotList != null)
                .SelectMany(r => r.HekimSlotList)
                .Where(h => h?.MuayeneYeriSlotList != null)
                .SelectMany(h => h.MuayeneYeriSlotList)
                .Where(y => y?.SaatSlotList != null)
                .SelectMany(y => y.SaatSlotList)
                .Where(g => g != null && g.Bos && g.SlotList != null)
                .SelectMany(g => g.SlotList)
                .Where(x => x != null && x.Bos && x.SubSlot != null)
                .Select(x => (Slot: x.SubSlot, Ok: DateTime.TryParse(x.SubSlot.BaslangicZamani, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt), Time: dt))
                .Where(t => t.Ok && t.Time >= min && t.Time <= max && t.Time > now)
                .OrderBy(t => t.Time)
                .Select(t => t.Slot)
                .FirstOrDefault();
        }

        /// <summary>GG-AA-YYYY (ör. 18-11-2026 veya 8-1-2026) biçimindeki tarihi okur.</summary>
        internal static bool TryParseTrDate(string? text, out DateTime date) =>
            DateTime.TryParseExact(text?.Trim(), new[] { "dd-MM-yyyy", "d-M-yyyy" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        static bool MakeAppointment(IClientService client, AppointmentRequestModel appointmentRequestModel, bool sendNotification)
        {
            var randevuResp = client.PostSimple(MHRSUrls.BaseUrl, MHRSUrls.MakeAppointment, appointmentRequestModel);

            // HTTP 200 tek başına yetmez: gövdedeki "success" alanı ve hata kodu da kontrol edilir
            bool bodySuccess = true;
            string? hataDetay = null;
            try
            {
                var body = randevuResp.Content.ReadAsStringAsync().Result;
                var parsed = System.Text.Json.JsonSerializer.Deserialize<ApiResponse<object>>(body);
                if (parsed != null && (!parsed.Success || (parsed.Errors != null && parsed.Errors.Length > 0)))
                {
                    bodySuccess = false;
                    var (kod, mesaj) = ApiErrorUtil.First(parsed.Errors);
                    hataDetay = $"{kod} {mesaj}".Trim();
                }
            }
            catch
            {
                // Gövde okunamazsa yalnızca HTTP koduna güvenilir (eski davranış)
            }

            if (randevuResp.StatusCode != HttpStatusCode.OK || !bodySuccess)
            {
                var errorMessage = $"❌ Randevu alırken bir problem ile karşılaşıldı!\nRandevu Tarihi: {appointmentRequestModel.BaslangicZamani}\nHTTP: {(int)randevuResp.StatusCode}{(hataDetay != null ? "\nMHRS: " + hataDetay : "")}";
                Console.WriteLine(errorMessage);
                
                if (sendNotification && _notificationService != null)
                {
                    _ = Task.Run(() => _notificationService.SendNotification(errorMessage));
                }
                return false;
            }

            var successMessage = $"✅ BAŞARILI! Randevu alındı!\n📅 Tarih: {appointmentRequestModel.BaslangicZamani}\n🕐 Alınma Zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            Console.WriteLine(successMessage);

            if (sendNotification && _notificationService != null)
            {
                // Süreç hemen ardından kapanıyor; bildirimin gitmesini bekle
                try { _notificationService.SendNotification(successMessage).Wait(TimeSpan.FromSeconds(20)); } catch { }
            }

            return true;
        }

        static (string? Kod, string? Mesaj) LAST_LOGIN_ERROR;

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

        /// <summary>
        /// Giriş başarısızsa: yanlış şifre gibi kalıcı hatalarda kod 2 ile çık (systemd yeniden
        /// başlatmaz, hatalı giriş sayısı artmaz, hesap kilitlenmez); geçici hatalarda kod 1.
        /// </summary>
        static void ExitForLoginFailure(string context)
        {
            var (kod, mesaj) = LAST_LOGIN_ERROR;
            if (ApiErrorUtil.IsPermanentLoginError(kod))
                ExitWith(2, $"❌ MHRS girişi reddedildi ({context}).\nKod: {kod}\n{mesaj}\n\nBot durduruldu ve yeniden denemeyecek (hesap kilitlenmesin). .env'deki TC/şifreyi düzeltip servisi yeniden başlatın.", notify: true);
            else
                ExitWith(1, $"MHRS girişi geçici olarak başarısız ({context}): {kod ?? "bağlantı"} {mesaj}", notify: false);
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

        static void RunSetupWizard(string? argTc = null, string? argPass = null)
        {
            Console.Clear();
            Console.WriteLine(@"
 __  __ _    _ _____   _____   ____  ____ _______ 
|  \/  | |  | |  __ \ / ____| |  _ \|  _ \__   __|
| \  / | |__| | |__) | (___   | |_) | | | | | |   
| |\/| |  __  |  _  / \___ \  |  _ <| | | | | |   
| |  | | |  | | | \ \ ____) | | |_) | |_| | | |   
|_|  |_|_|  |_|_|  \_\_____/  |____/|____/  |_|   
                                                  
MHRS OTO RANDEVU - KURULUM SİHİRBAZI
=============================================
");

            _client = new ClientService();
            _notificationService = new NotificationService();

            // VAROLAN AYARLARI KONTROL ET (Partial Update Mode)
            bool quickMode = false;
            string existingTc = "";
            string existingPwd = "";
            string existingTelToken = "";
            string existingTelChatId = "";
            string existingSchedule = "";

            // Eğer argüman olarak TC/Şifre geldiyse direkt onları kullan
            if (!string.IsNullOrEmpty(argTc) && !string.IsNullOrEmpty(argPass))
            {
                quickMode = true;
                TC_NO = argTc;
                SIFRE = argPass;
                Console.WriteLine($"[BİLGİ] Seçilen kullanıcı ile oturum açılıyor... ({argTc.Substring(0,2)}***)");
            }

            // Dosyadan oku (Eğer arg yoksa veya Telegram ayarlarını almak için)
            if (File.Exists(".env"))
            {
                foreach (var line in File.ReadAllLines(".env"))
                {
                    if (line.StartsWith("MHRS_TC=") && string.IsNullOrEmpty(existingTc)) existingTc = line.Split('=')[1].Trim();
                    if (line.StartsWith("MHRS_PASSWORD=") && string.IsNullOrEmpty(existingPwd)) existingPwd = line.Split('=')[1].Trim();
                    if (line.StartsWith("TELEGRAM_BOT_TOKEN=")) existingTelToken = line.Split('=')[1].Trim();
                    if (line.StartsWith("TELEGRAM_CHAT_ID=")) existingTelChatId = line.Split('=')[1].Trim();
                    if (line.StartsWith("MHRS_SCHEDULE_TIMES=")) existingSchedule = line.Split('=')[1].Trim();
                }
            }

            // GECMIS KULLANICILAR VE SECIM EKRANI
            if (!quickMode)
            {
                var historyUsers = LoadUserHistory();
                if (historyUsers.Count > 0)
                {
                    Console.WriteLine("\n--- KAYITLI HESAPLAR (Son 3) ---");
                    int i = 1;
                    foreach (var u in historyUsers)
                    {
                        string maskTc = u.tc.Length > 4 ? $"{u.tc.Substring(0,2)}*******{u.tc.Substring(u.tc.Length-2)}" : u.tc;
                        Console.WriteLine($"{i}. {maskTc} (Son: {u.date})");
                        i++;
                    }
                    Console.WriteLine($"{i}. Yeni Hesap Ekle / Farklı Giriş");
                    Console.WriteLine($"{i+1}. Geçmişi Temizle (Güvenlik)");
                    
                    Console.Write($"Seçiminiz (1-{i+1}): ");
                    if (int.TryParse(Console.ReadLine(), out int choice) && choice > 0 && choice <= i+1)
                    {
                        if (choice < i) // User selected from history
                        {
                            var selected = historyUsers[choice - 1];
                            TC_NO = selected.tc;
                            SIFRE = selected.pwd;
                            quickMode = true;
                            Console.WriteLine($"✅ {selected.tc.Substring(0,2)}*** seçildi, giriş yapılıyor...");
                        }
                        else if (choice == i + 1) // Clear History
                        {
                            try { File.Delete(".user_history"); } catch { }
                            Console.WriteLine("🗑️ Geçmiş temizlendi. Yeni giriş bekleniyor...");
                            // Fall through to manual entry
                        }
                        // Else (choice == i) -> Find New -> Fall through
                    }
                }
                // Eğer geçmiş yoksa ama .env varsa (Eski davranış)
                else if (!string.IsNullOrEmpty(existingTc) && !string.IsNullOrEmpty(existingPwd))
                {
                    Console.WriteLine($"[BİLGİ] Kayıtlı kullanıcı bulundu: {existingTc.Substring(0,2)}*******{existingTc.Substring(existingTc.Length-2)}");
                    Console.Write("Mevcut giriş bilgileri kullanılsın mı? (E/h): ");
                    if (Console.ReadLine()?.ToLower().StartsWith("e") != false) // Default Evet
                    {
                        quickMode = true;
                        TC_NO = existingTc;
                        SIFRE = existingPwd;
                        Console.WriteLine("✅ Mevcut kullanıcı ile devam ediliyor...");
                    }
                }
            }

            // 1. GİRİŞ BİLGİLERİ (Eğer Hızlı Mod Değilse)
            if (!quickMode)
            {
                Console.WriteLine("\nLütfen MHRS giriş bilgilerinizi giriniz:");
                Console.Write("TC Kimlik No: ");
                string tc = Console.ReadLine()?.Trim() ?? "";
                
                Console.Write("e-Devlet / MHRS Şifresi: ");
                string sifre = ReadPassword();

                if (string.IsNullOrEmpty(tc) || string.IsNullOrEmpty(sifre))
                {
                    Console.WriteLine("Hata: TC ve şifre boş olamaz!");
                    return;
                }
                TC_NO = tc;
                SIFRE = sifre;
            }

            // Giriş denemesi
            Console.WriteLine("\nGiriş yapılıyor, lütfen bekleyin...");
            try 
            {
                var tokenResult = GetToken(_client);
                if (tokenResult == null || string.IsNullOrEmpty(tokenResult.Token))
                {
                    Console.WriteLine("Giriş başarısız! TC veya şifre hatalı.");
                    return;
                }
                JWT_TOKEN = tokenResult.Token;
                _client.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
                Console.WriteLine("✅ Giriş başarılı!\n");
                
                // Geçmişe kaydet
                SaveUserToHistory(TC_NO, SIFRE);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Giriş hatası: {ex.Message}");
                return;
            }

            // 2. İL SEÇİMİ
            Console.WriteLine("--- ŞEHİR SEÇİMİ ---");
            var provinceListResponse = _client.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, MHRSUrls.GetProvinces);
            if (provinceListResponse == null) { Console.WriteLine("İl listesi alınamadı!"); return; }
            
            var provinceList = provinceListResponse.OrderBy(x => x.ValueAsInt).ToList();
            
            // İl listesini göster
            foreach (var p in provinceList)
            {
                Console.WriteLine($"{p.ValueAsInt} - {p.Text}");
            }
            
            int provinceId = -1;
            while(provinceId == -1)
            {
                Console.Write("İl Plaka Kodu (Örn: İstanbul için 34): ");
                if(int.TryParse(Console.ReadLine(), out int plaka) && plaka > 0 && plaka <= 81)
                {
                    provinceId = plaka;
                    // İstanbul kontrolü
                    if (plaka == 34)
                    {
                        Console.WriteLine("1. İstanbul (Avrupa) - 341");
                        Console.WriteLine("2. İstanbul (Anadolu) - 342");
                        Console.Write("Seçiminiz (1 veya 2): ");
                        var sub = Console.ReadLine();
                        provinceId = sub == "2" ? 342 : 341;
                    }
                }
                else
                {
                    Console.WriteLine("Geçersiz plaka kodu!");
                }
            }

            // 3. İLÇE SEÇİMİ
            Console.WriteLine($"\n--- İLÇE SEÇİMİ (İl ID: {provinceId}) ---");
            var districtList = _client.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDistricts, provinceId));
            int districtId = -1;
            
            if (districtList != null && districtList.Count > 0)
            {
                Console.WriteLine("0 - TÜM İLÇELER (Farketmez)");
                for (int i = 0; i < districtList.Count; i++)
                {
                    Console.WriteLine($"{i+1} - {districtList[i].Text}");
                }
                
                while(true)
                {
                    Console.Write("İlçe Seçiminiz (Sıra No): ");
                    if(int.TryParse(Console.ReadLine(), out int distIdx))
                    {
                        if(distIdx == 0) { districtId = -1; break; }
                        if(distIdx > 0 && distIdx <= districtList.Count)
                        {
                            districtId = districtList[distIdx-1].ValueAsInt;
                            break;
                        }
                    }
                    Console.WriteLine("Geçersiz seçim!");
                }
            }

            // 4. KLİNİK SEÇİMİ
            Console.WriteLine($"\n--- KLİNİK SEÇİMİ ---");
            var clinicListResp = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetClinics, provinceId, districtId));
            int clinicId = -1;

            if (clinicListResp.Success && clinicListResp.Data != null)
            {
                var clinics = clinicListResp.Data.OrderBy(x => x.Text).ToList();
                for (int i = 0; i < clinics.Count; i++)
                {
                    Console.WriteLine($"{i+1} - {clinics[i].Text}");
                }

                while(clinicId == -1)
                {
                    Console.Write("Klinik Seçiminiz (Sıra No): ");
                    if(int.TryParse(Console.ReadLine(), out int cIdx) && cIdx > 0 && cIdx <= clinics.Count)
                    {
                        clinicId = clinics[cIdx-1].ValueAsInt;
                    }
                    else
                    {
                        Console.WriteLine("Geçersiz seçim!");
                    }
                }
            }
            else 
            {
                Console.WriteLine("Klinik listesi alınamadı!");
                return;
            }

            // 5. HASTANE SEÇİMİ
            Console.WriteLine($"\n--- HASTANE SEÇİMİ ---");
            var hospitalListResp = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetHospitals, provinceId, districtId, clinicId));
            int hospitalId = -1;
            
            if (hospitalListResp.Success && hospitalListResp.Data != null)
            {
                 var hospitals = hospitalListResp.Data;
                 Console.WriteLine("0 - TÜM HASTANELER (Farketmez)");
                 for (int i = 0; i < hospitals.Count; i++)
                 {
                     Console.WriteLine($"{i+1} - {hospitals[i].Text}");
                 }
                 
                 while(true)
                 {
                    Console.Write("Hastane Seçiminiz (Sıra No): ");
                    if(int.TryParse(Console.ReadLine(), out int hIdx))
                    {
                        if(hIdx == 0) { hospitalId = -1; break; }
                        if(hIdx > 0 && hIdx <= hospitals.Count)
                        {
                            hospitalId = hospitals[hIdx-1].ValueAsInt;
                            break;
                        }
                    }
                 }
            }

            // 6. DOKTOR SEÇİMİ
            // Doktor listesi çok uzun olabilir, sadece hastane seçildiyse veya kullanıcı isterse
            Console.WriteLine($"\n--- DOKTOR SEÇİMİ ---");
            Console.WriteLine("0 - TÜM DOKTORLAR (Farketmez - Önerilen)");
            Console.WriteLine("1 - Doktor Seçmek İstiyorum");
            Console.Write("Seçiminiz: ");
            int doctorId = -1;
            
            if(Console.ReadLine() == "1")
            {
                var docResp = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDoctors, hospitalId, clinicId));
                 if (docResp.Success && docResp.Data != null && docResp.Data.Count > 0)
                 {
                     var docs = docResp.Data;
                     for (int i = 0; i < docs.Count; i++)
                        Console.WriteLine($"{i+1} - {docs[i].Text}");
                        
                     Console.Write("Doktor Seçiminiz (Sıra No veya 0): ");
                     if(int.TryParse(Console.ReadLine(), out int dIdx) && dIdx > 0 && dIdx <= docs.Count)
                     {
                         doctorId = docs[dIdx-1].ValueAsInt;
                     }
                 }
                 else
                 {
                     Console.WriteLine("Bu kriterlere uygun doktor bulunamadı veya liste boş.");
                 }
            }

            // 7. TARİH ARALIĞI SEÇİMİ
            Console.WriteLine($"\n--- TARİH ARALIĞI SEÇİMİ ---");
            Console.WriteLine("Randevu hangi tarihten itibaren aransın? (GG-AA-YYYY)");
            Console.WriteLine($"Boş bırakırsanız BUGÜN ({DateTime.Now:dd-MM-yyyy}) kabul edilir.");
            Console.Write("Başlangıç Tarihi: ");
            string? inputStartDate = Console.ReadLine()?.Trim();
            string mhrsStartDate = string.IsNullOrEmpty(inputStartDate) ? DateTime.Now.ToString("dd-MM-yyyy") : inputStartDate;

            Console.WriteLine("\nKaç gün sonrasına kadar aransın? (Örn: 15)");
            Console.WriteLine("Boş bırakırsanız varsayılan (15 gün) kabul edilir.");
            Console.Write("Gün Sayısı: ");
            string? inputDays = Console.ReadLine()?.Trim();
            int daysToAdd = 15;
            if (!string.IsNullOrEmpty(inputDays) && int.TryParse(inputDays, out int d) && d > 0) daysToAdd = d;
            
            // Bitiş tarihini hesapla (Sadece bilgi amaçlı, asıl tarih .env'ye yazılacak)
            DateTime startDt;
            try 
            {
               var parts = mhrsStartDate.Split('-');
               startDt = new DateTime(int.Parse(parts[2]), int.Parse(parts[1]), int.Parse(parts[0]));
            }
            catch
            {
                Console.WriteLine($"[UYARI] '{mhrsStartDate}' geçersiz, bugün kullanılıyor.");
                startDt = DateTime.Now;
                mhrsStartDate = startDt.ToString("dd-MM-yyyy");
            }
            DateTime endDt = startDt.AddDays(daysToAdd);
            string mhrsEndDate = endDt.ToString("dd-MM-yyyy");
            
            Console.WriteLine($"[BİLGİ] {mhrsStartDate} ile {mhrsEndDate} arasındaki randevular aranacak.");


            // 8. ZAMANLAMA SEÇİMİ (Opsiyonel)
            string schedule = existingSchedule; // Hızlı modda varsayılan olarak eskisi korunur
            
            if (!quickMode)
            {
                Console.WriteLine($"\n--- SAAT / DAKİKA KONTROLÜ ---");
                Console.WriteLine("Botun hangi saatlerde çalışmasını istersiniz?");
                Console.WriteLine("Örnek: 09:55,09:59,10:00,15:55,16:00 (Virgülle ayırın)");
                Console.WriteLine("Boş bırakırsanız varsayılan algoritma (her saat başı ve rastgele) çalışır.");
                Console.Write("Saatler: ");
                schedule = Console.ReadLine()?.Trim() ?? "";
            }
            else
            {
                 // Hızlı Modda kullanıcıya sormadan eski ayarı veya boşu kullan
                 Console.WriteLine($"\n[BİLGİ] Saat zamanlaması eski ayarlardan yüklendi ({ (string.IsNullOrEmpty(schedule) ? "Akıllı Mod" : schedule) })");
            }

            Console.WriteLine("\nSaat pencerelerini beklemeden SÜREKLİ arasın mı? (kısa tarih aralıkları için önerilir) (e/H)");
            Console.Write("Sürekli mod: ");
            string continuous = Console.ReadLine()?.Trim().ToLower().StartsWith("e") == true ? "1" : "0";

            // 9. TELEGRAM BİLDİRİM AYARLARI
            string telegramToken = existingTelToken;
            string telegramChatId = existingTelChatId;
            
            // Eğer Hızlı Moddaysa (Kullanıcı verildiyse) ve Telegram zaten varsa ASLA sorma
            if (quickMode && !string.IsNullOrEmpty(existingTelToken))
            {
                // Sessizce geç
            }
            else
            {
                Console.WriteLine($"\n--- TELEGRAM BİLDİRİM AYARLARI ---");
                Console.WriteLine("Randevu alındığında bildirim almak için.");
                Console.Write("Telegram ayarlarını yapılandırmak istiyor musunuz? (e/H): ");
                
                if (Console.ReadLine()?.ToLower().StartsWith("e") == true)
                {
                    Console.Write("Telegram Bot Token: ");
                    string inputToken = Console.ReadLine()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(inputToken)) telegramToken = inputToken;
                    
                    Console.Write("Telegram Chat ID: ");
                    string inputChatId = Console.ReadLine()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(inputChatId)) telegramChatId = inputChatId;
                }
            }

            // .ENV DOSYASI OLUŞTURMA
            string envContent = $@"# MHRS Otomatik Randevu - Yapılandırma Dosyası
# Oluşturulma Tarihi: {DateTime.Now}

# GİRİŞ BİLGİLERİ
MHRS_TC={TC_NO}
MHRS_PASSWORD={SIFRE}

# KONUM VE POLİKLİNİK BİLGİLERİ
MHRS_PROVINCE_ID={provinceId}
MHRS_DISTRICT_ID={districtId}
MHRS_CLINIC_ID={clinicId}
MHRS_HOSPITAL_ID={hospitalId}
MHRS_PLACE_ID=-1
MHRS_DOCTOR_ID={doctorId}

# TARİH VE ZAMANLAMA
# Başlangıç tarihi (GG-AA-YYYY) boş bırakılırsa 'bugün' olur
MHRS_START_DATE={mhrsStartDate}
# Bitiş tarihi (GG-AA-YYYY) boş bırakılırsa 15 gün sonrası olur
MHRS_END_DATE={mhrsEndDate}
# Özel zamanlama (Boş ise akıllı mod çalışır)
MHRS_SCHEDULE_TIMES={schedule}
# 1 = saat pencerelerini beklemeden sürekli ara. Bitiş tarihi geçince bot kendini durdurur.
MHRS_CONTINUOUS={continuous}
# İki deneme arası bekleme (sn, en az 30)
MHRS_INTERVAL_SECONDS=120

# BİLDİRİM AYARLARI (Opsiyonel)
TELEGRAM_BOT_TOKEN={telegramToken}
TELEGRAM_CHAT_ID={telegramChatId}
TELEGRAM_NOTIFY_FREQUENCY=10
";
            
            File.WriteAllText(".env", envContent);
            Console.WriteLine("\n✅ .env dosyası başarıyla oluşturuldu!");
            Console.WriteLine("Kurulum tamamlandı. Botu başlatmak için ./run.sh veya ./bot.sh kullanabilirsiniz.");
        }

        static void SaveUserToHistory(string tc, string password)
        {
            try
            {
                string historyFile = ".user_history";
                var history = new List<string>();
                
                if (File.Exists(historyFile))
                {
                    history = File.ReadAllLines(historyFile).ToList();
                }

                history.RemoveAll(line => line.StartsWith(tc + "|"));
                history.Insert(0, $"{tc}|{password}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                if (history.Count > 3)
                {
                    history = history.Take(3).ToList();
                }

                File.WriteAllLines(historyFile, history);
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"[UYARI] Geçmiş kaydedilemedi: {ex.Message}");
            }
        }

        static List<(string tc, string pwd, string date)> LoadUserHistory()
        {
            var list = new List<(string, string, string)>();
            bool needsUpdate = false;
            try
            {
                if (File.Exists(".user_history"))
                {
                    var lines = File.ReadAllLines(".user_history").ToList();
                    var validLines = new List<string>();

                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 3)
                        {
                            // 15 Günlük Süre Kontrolü (Güvenlik)
                            if (DateTime.TryParse(parts[2], out DateTime lastLogin) && (DateTime.Now - lastLogin).TotalDays <= 15)
                            {
                                list.Add((parts[0], parts[1], parts[2]));
                                validLines.Add(line);
                            }
                            else
                            {
                                needsUpdate = true; // Süresi dolmuş kayıt bulundu
                            }
                        }
                    }

                    // Eğer temizlenen kayıt varsa dosyayı güncelle
                    if (needsUpdate)
                    {
                         File.WriteAllLines(".user_history", validLines);
                    }
                }
            }
            catch { }
            return list;
        }

        // Konsolda şifreyi gizli okuma fonksiyonu
        static string ReadPassword()
        {
            var pwd = string.Empty;
            ConsoleKeyInfo key;
            do
            {
                key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && pwd.Length > 0)
                {
                    pwd = pwd[..^1];
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    pwd += key.KeyChar;
                    Console.Write("*");
                }
            } while (true);
            Console.WriteLine();
            return pwd;
        }

    }
}