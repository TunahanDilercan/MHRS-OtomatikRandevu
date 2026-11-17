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
    // Slot arama, en erken slot seçimi ve randevu alma
    public partial class Program
    {
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
    }
}
