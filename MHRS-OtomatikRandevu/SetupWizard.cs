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
    // Kurulum sihirbazı (--setup): .env dosyasını etkileşimli oluşturur
    public partial class Program
    {
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
