using System;
using System.Collections.Generic;
using System.Linq;

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

Console.WriteLine("Rastgele Saat Aralıkları Testi");
Console.WriteLine("==============================");

// Bugün için birkaç saat testi
for (int hour = 0; hour < 24; hour++)
{
    var times = GetRandomTimesForHour(hour);
    Console.WriteLine($"Saat {hour:D2}: {string.Join(", ", times.Select(t => $"{hour:D2}:{t:D2}"))}");
}

Console.WriteLine("\nBugün saat 15:30'da çalışır mı?");
Console.WriteLine($"Sonuç: {IsInRandomMidHourWindow(15, 30)}");

Console.WriteLine("\nBugün saat 08:25'de çalışır mı?");
Console.WriteLine($"Sonuç: {IsInRandomMidHourWindow(8, 25)}");
