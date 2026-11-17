using MHRS_OtomatikRandevu.Models;
using System.Text;
using System.Text.Json;

namespace MHRS_OtomatikRandevu.Utils
{
    public static class JwtTokenUtil
    {
        /// <summary>JWT'nin "exp" alanından son geçerlilik zamanını (yerel saat) okur. Okunamazsa MinValue.</summary>
        public static DateTime GetTokenExpireTime(string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                    return DateTime.MinValue;
                if (!token.Contains("."))
                    return DateTime.MinValue;

                string[] parts = token.Split('.');
                var payload = JsonSerializer.Deserialize<JwtTokenModel>(Base64UrlDecode(parts[1]));
                DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(payload!.ExpirationUnix);
                return dateTimeOffset.LocalDateTime;
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>Base64Url (RFC 4648 §5) çözümü; Microsoft.IdentityModel bağımlılığı yerine.</summary>
        internal static string Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
    }
}
