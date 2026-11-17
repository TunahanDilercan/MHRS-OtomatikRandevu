using System.Text.Json;

namespace MHRS_OtomatikRandevu.Utils
{
    /// <summary>
    /// MHRS hata nesnelerini ({"kodu":"RND4010","mesaj":"..."}) okur.
    /// ApiResponse.Errors object[] olarak geldiği için elemanlar JsonElement'tir.
    /// </summary>
    public static class ApiErrorUtil
    {
        public static (string? Kod, string? Mesaj) First(object[]? errors)
        {
            if (errors == null || errors.Length == 0)
                return (null, null);

            var e = errors[0];
            if (e is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                string? kod = el.TryGetProperty("kodu", out var k) ? k.ToString() : null;
                string? mesaj = el.TryGetProperty("mesaj", out var m) ? m.ToString() : null;
                return (kod, mesaj);
            }
            return (null, e?.ToString());
        }

        public static bool Contains(object[]? errors, string kod) =>
            errors != null && errors.Any(e => e?.ToString()?.Contains(kod) == true);

        /// <summary>
        /// Tekrar denemenin anlamsız olduğu giriş hataları (yanlış TC/şifre, hesap kilidi vb.).
        /// Bunlarda bot yeniden başlatılmamalı; aksi halde her denemede hatalı giriş sayısı artar.
        /// </summary>
        public static bool IsPermanentLoginError(string? kod) =>
            kod != null && (kod.StartsWith("VT") || (kod.StartsWith("LGN") && kod != "LGN2001"));
    }
}
