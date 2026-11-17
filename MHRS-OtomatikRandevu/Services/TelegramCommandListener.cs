using System.Net;
using System.Text.Json;

namespace MHRS_OtomatikRandevu.Services
{
    /// <summary>
    /// Telegram getUpdates ile (long polling) yalnızca TELEGRAM_CHAT_ID sohbetinden gelen "/" komutlarını dinler.
    /// Başlarken birikmiş eski mesajları atlar (yeniden başlatmada eski komutlar tekrar çalışmasın).
    /// Aynı bot token'ını başka bir süreç dinliyorsa (HTTP 409) uyarı verip kendini kapatır.
    /// </summary>
    public sealed class TelegramCommandListener
    {
        readonly string _token;
        readonly string _chatId;
        readonly Func<string, string> _handle;
        readonly Func<string, Task> _reply;
        readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };
        long _offset;

        public TelegramCommandListener(string token, string chatId, Func<string, string> handle, Func<string, Task> reply)
        {
            _token = token;
            _chatId = chatId;
            _handle = handle;
            _reply = reply;
        }

        public void Start() => _ = Task.Run(RunAsync);

        async Task RunAsync()
        {
            try
            {
                // Birikmiş güncellemeleri atla
                var initial = await GetUpdates(-1, 0);
                if (initial != null && initial.Value.GetArrayLength() > 0)
                    _offset = initial.Value[initial.Value.GetArrayLength() - 1].GetProperty("update_id").GetInt64() + 1;
            }
            catch { /* ilk deneme başarısızsa döngü yeniden dener */ }

            Console.WriteLine("[INFO] Telegram komutları dinleniyor (/yardim).");
            while (true)
            {
                try
                {
                    var updates = await GetUpdates(_offset, 25);
                    if (updates == null)
                        return;   // 409: başka dinleyici var, kapat

                    foreach (var u in updates.Value.EnumerateArray())
                    {
                        _offset = u.GetProperty("update_id").GetInt64() + 1;
                        if (!u.TryGetProperty("message", out var msg)) continue;
                        if (!msg.TryGetProperty("chat", out var chat) || chat.GetProperty("id").ToString() != _chatId) continue;
                        if (!msg.TryGetProperty("text", out var textEl)) continue;
                        var text = textEl.GetString() ?? "";
                        if (!text.StartsWith("/")) continue;

                        string answer;
                        try { answer = _handle(text); }
                        catch (Exception ex) { answer = $"Komut işlenemedi: {ex.Message}"; }
                        await _reply(answer);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UYARI] Telegram komut dinleme hatası: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(15));
                }
            }
        }

        /// <returns>result dizisi; 409 (başka dinleyici / webhook) ise null.</returns>
        async Task<JsonElement?> GetUpdates(long offset, int timeoutSec)
        {
            var url = $"https://api.telegram.org/bot{_token}/getUpdates?offset={offset}&timeout={timeoutSec}&allowed_updates=%5B%22message%22%5D";
            using var resp = await _http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.Conflict)
            {
                Console.WriteLine("[UYARI] Bu Telegram bot token'ını başka bir süreç dinliyor (409). Komutlar bu bot için kapatıldı; bildirimler çalışmaya devam eder.");
                return null;
            }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("result").Clone();
        }
    }
}
