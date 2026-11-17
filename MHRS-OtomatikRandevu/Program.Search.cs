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
    // Randevu arama döngüsü: zamanlama, slot arama, randevu alma ve bildirimler
    public partial class Program
    {
        static void RunSearchLoop(SearchTarget target)
        {
            int provinceIndex = target.Province, districtIndex = target.District, clinicIndex = target.Clinic;
            int hospitalIndex = target.Hospital, placeIndex = target.Place;
            string doctorIndex = target.DoctorsText;
            // Her sabah 09:00'da bir kez durum özeti; bot 09:00'dan sonra başladıysa ilk rapor ertesi sabah
            DateTime lastDailyReport = DateTime.Now.Hour >= 9 ? DateTime.Today : DateTime.Today.AddDays(-1);

            #region Randevu Alım Bölümü
            ConsoleUtil.WriteText("Yapmış olduğunuz seçimler doğrultusunda müsait olan ilk randevu otomatik olarak alınacaktır.", 3000);
            Console.Clear();
            
            Console.WriteLine("=== MHRS Otomatik Randevu Botu ===");
            Console.WriteLine($"Başlangıç Zamanı: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Arama Kriterleri: İl({provinceIndex}) İlçe({districtIndex}) Klinik({clinicIndex})");
            Console.WriteLine($"Hastane({hospitalIndex}) Yer({placeIndex}) Doktor({doctorIndex})");
            Console.WriteLine($"Tarih Aralığı: {DateRangeText()}");
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

                if (DAILY_REPORT && DateTime.Now.Hour >= 9 && lastDailyReport < DateTime.Today && _notificationService != null)
                {
                    lastDailyReport = DateTime.Today;
                    var daily = $"☀️ Günlük durum: bot çalışıyor{(PAUSED ? " (DURAKLATILDI)" : "")}\n\nDeneme: {attemptCount} (toplam hata: {STATE_ERRORS_TOTAL})\nSon sonuç: {STATE_LAST_RESULT}\nTarih: {DateRangeText()}";
                    _ = Task.Run(() => _notificationService.SendNotification(daily));
                }

                // /duraklat komutu: aramayı beklet
                if (PAUSED)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    continue;
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
                    ApplyToken(tkn);
                    LogStatus("Token yenilendi", null, true);
                }

                attemptCount++;

                SubSlot? slot = null;
                SlotSearchResult? search = null;
                try
                {
                    search = CallWithSessionRecovery(() => SearchSlots(target), $"slot arama #{attemptCount}", notifyRecovery: true);
                    slot = search.Slot;
                }
                catch (SessionExpiredException ex)
                {
                    // Yeniden girişten hemen sonra oturum yine düştü (başka cihazdan giriş / çoklu bot?)
                    Console.WriteLine($"[ERROR] ❌ Session expired again after recovery: {ex.Message}");
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
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] ❌ Unexpected error during slot request: {ex.Message}");
                    LogStatus($"Unexpected error during slot request: {ex.Message} (Deneme #{attemptCount})", null, true);
                    
                    // Skip this iteration
                    Thread.Sleep(TimeSpan.FromMinutes(1));
                    continue;
                }
                RecordAttempt(attemptCount, search);

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
                    appointmentResult = CallWithSessionRecovery(() => MakeAppointment(_client!, appointmentRequestModel, sendNotification: true), $"randevu alma #{attemptCount}", notifyRecovery: true);
                }
                catch (SessionExpiredException ex)
                {
                    Console.WriteLine($"[ERROR] ❌ Session expired again during appointment retry: {ex.Message}");
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
        }
    }
}
