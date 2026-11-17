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
    // Arama saat pencereleri (MHRS_SCHEDULE_TIMES veya varsayılan akıllı mod)
    public partial class Program
    {
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
    }
}
