using MHRS_OtomatikRandevu.Exceptions;
using MHRS_OtomatikRandevu.Models.ResponseModels;
using MHRS_OtomatikRandevu.Urls;

namespace MHRS_OtomatikRandevu
{
    /// <summary>MHRS'de doğrulanmış arama hedefi. -1 = FARKETMEZ.</summary>
    internal record SearchTarget(int Province, int District, int Clinic, int Hospital, int Place, int Doctor);

    // .env'deki il / ilçe / klinik / hastane / muayene yeri / doktor ID'lerini MHRS listeleriyle doğrular
    public partial class Program
    {
        /// <summary>
        /// Ayarlardaki ID'leri MHRS'den gelen listelerle eşleştirir. Geçersiz bir ID'de geçerli seçenekleri
        /// yazdırır ve null döner (çağıran kod 2 ile çıkar). Liste alınamazsa geçici hata (kod 1) ile çıkar.
        /// </summary>
        static SearchTarget? ResolveTarget(BotConfig cfg)
        {
            // İl
            int provinceIndex = cfg.ProvinceId;
            List<GenericResponseModel>? provinceListResponse;
            try
            {
                provinceListResponse = CallWithSessionRecovery(
                    () => _client!.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, MHRSUrls.GetProvinces), "il listesi");
            }
            catch (SessionExpiredException retryEx)
            {
                Console.WriteLine($"[ERROR] ❌ Session expired again after recovery: {retryEx.Message}");
                Console.WriteLine("[ERROR] This suggests multiple logins or system issues. Please check:");
                Console.WriteLine("  1. Make sure no other instances of the bot are running");
                Console.WriteLine("  2. Make sure you're not logged in from another browser/device");
                Console.WriteLine("  3. Try running the bot later if MHRS system is under maintenance");
                LogStatus("Session expired again after recovery during initial setup", null, true);
                return null;
            }

            if (provinceListResponse == null || !provinceListResponse.Any())
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return null;
            }
            var provinceList = provinceListResponse
                                    .DistinctBy(x => x.ValueAsInt)
                                    .OrderBy(x => x.ValueAsInt)
                                    .ToList();
            var istanbulSubLocationIds = new int[] { 341, 342 };
            if (!provinceList.Any(x => x.ValueAsInt == provinceIndex) && !istanbulSubLocationIds.Contains(provinceIndex))
            {
                Console.WriteLine($"[ERROR] MHRS_PROVINCE_ID ({provinceIndex}) geçersiz. Lütfen .env dosyasını ve plaka kodunu kontrol edin.");
                Console.WriteLine("İpucu: İstanbul Avrupa için 341, Anadolu için 342, diğer iller için plaka kodu kullanın.");
                return null;
            }

            // İlçe
            List<GenericResponseModel>? districtList;
            try
            {
                districtList = CallWithSessionRecovery(
                    () => _client!.GetSimple<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDistricts, provinceIndex)), "ilçe listesi");
            }
            catch (SessionExpiredException retryEx)
            {
                Console.WriteLine($"[ERROR] ❌ Session expired again after recovery: {retryEx.Message}");
                LogStatus("Session expired again after recovery during district setup", null, true);
                return null;
            }

            if (districtList == null || !districtList.Any())
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return null;
            }
            int? districtIndex = cfg.DistrictId == null ? null : MatchOptionalId(cfg.DistrictId.Value, districtList.Select(x => x.ValueAsInt));
            if (districtIndex == null)
            {
                Console.WriteLine("[ERROR] MHRS_DISTRICT_ID ayarlanmamış veya geçersiz. İsterseniz -1 (Farketmez) yapabilirsiniz.");
                PrintOptions(districtList, withAny: true);
                return null;
            }

            // Klinik (zorunlu)
            var clinicListResponse = _client!.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetClinics, provinceIndex, districtIndex));
            if (!clinicListResponse.Success && (clinicListResponse.Data == null || !clinicListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return null;
            }
            var clinicList = clinicListResponse.Data;
            var clinic = cfg.ClinicId is int cid ? clinicList.FirstOrDefault(x => x.ValueAsInt == cid) : null;
            if (clinic == null)
            {
                Console.WriteLine("[ERROR] MHRS_CLINIC_ID ayarlanmamış veya geçersiz. Bir klinik seçmelisiniz.");
                Console.WriteLine("Mevcut klinikler:");
                foreach (var c in clinicList) Console.WriteLine($"  ID: {c.ValueAsInt} - {c.Text}");
                return null;
            }
            int clinicIndex = clinic.ValueAsInt;

            // Hastane
            var hospitalListResponse = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetHospitals, provinceIndex, districtIndex, clinicIndex));
            if (!hospitalListResponse.Success && (hospitalListResponse.Data == null || !hospitalListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return null;
            }
            var hospitalList = hospitalListResponse.Data;
            int? hospitalIndex = MatchOptionalId(cfg.HospitalId, hospitalList.Select(x => x.ValueAsInt));
            if (hospitalIndex == null)
            {
                // Yanlış ID'de sessizce FARKETMEZ'e düşmek yerine dur
                Console.WriteLine($"[ERROR] MHRS_HOSPITAL_ID={cfg.HospitalId} listede yok. Geçerli seçenekler:");
                PrintOptions(hospitalList, withAny: true);
                return null;
            }

            // Muayene yeri
            var placeListResponse = _client.Get<List<ClinicResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetPlaces, hospitalIndex, clinicIndex));
            if (!placeListResponse.Success && (placeListResponse.Data == null || !placeListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return null;
            }
            var placeList = placeListResponse.Data;
            int? placeIndex = MatchOptionalId(cfg.PlaceId, placeList.Select(x => x.ValueAsInt));
            if (placeIndex == null)
            {
                Console.WriteLine($"[ERROR] MHRS_PLACE_ID={cfg.PlaceId} listede yok. Geçerli seçenekler:");
                Console.WriteLine("  -1 - FARKETMEZ");
                foreach (var x in placeList) Console.WriteLine($"  {x.ValueAsInt} - {x.Text}");
                return null;
            }

            // Doktor
            var doctorListResponse = _client.Get<List<GenericResponseModel>>(MHRSUrls.BaseUrl, string.Format(MHRSUrls.GetDoctors, hospitalIndex, clinicIndex));
            if (!doctorListResponse.Success && (doctorListResponse.Data == null || !doctorListResponse.Data.Any()))
            {
                ExitWith(1, "MHRS'den liste alınamadı (bağlantı/bakım). Bir süre sonra yeniden denenecek.", notify: false);
                return null;
            }
            var doctorList = doctorListResponse.Data;
            int? doctorIndex = MatchOptionalId(cfg.DoctorId, doctorList.Select(x => x.ValueAsInt));
            if (doctorIndex == null)
            {
                Console.WriteLine($"[ERROR] MHRS_DOCTOR_ID={cfg.DoctorId} listede yok. Geçerli seçenekler:");
                PrintOptions(doctorList, withAny: true);
                return null;
            }

            return new SearchTarget(provinceIndex, districtIndex.Value, clinicIndex, hospitalIndex.Value, placeIndex.Value, doctorIndex.Value);
        }

        /// <summary>
        /// FARKETMEZ (-1) her zaman geçerlidir; diğer ID'ler listede olmalıdır. Listede yoksa null.
        /// </summary>
        internal static int? MatchOptionalId(int envId, IEnumerable<int> available) =>
            envId == BotConfig.Any ? BotConfig.Any : available.Contains(envId) ? envId : null;

        static void PrintOptions(IEnumerable<GenericResponseModel> list, bool withAny)
        {
            if (withAny) Console.WriteLine("  -1 - FARKETMEZ");
            foreach (var x in list) Console.WriteLine($"  {x.ValueAsInt} - {x.Text}");
        }
    }
}
