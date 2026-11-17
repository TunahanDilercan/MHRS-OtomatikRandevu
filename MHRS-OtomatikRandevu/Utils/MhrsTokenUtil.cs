using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MHRS_OtomatikRandevu.Utils
{
    /// <summary>
    /// Kullanıcının tarayıcıdan kopyaladığı MHRS oturumunu bota aktarmak için.
    /// mhrs.gov.tr, hangi yöntemle girilirse girilsin (şifre / e-Devlet / e-Nabız) JWT'yi
    /// localStorage["token-v-mhrs"] içinde CryptoJS AES ile (sabit parola "mhrs-secret-key",
    /// OpenSSL "Salted__" biçimi) saklar. Bu sınıf hem ham JWT'yi hem de bu şifreli değeri kabul eder.
    /// </summary>
    public static class MhrsTokenUtil
    {
        const string WebStoragePassphrase = "mhrs-secret-key";   // mhrs.gov.tr istemci kodundaki sabit

        public static bool TryExtractJwt(string? input, DateTime now, out string jwt, out string error)
        {
            jwt = "";
            error = "";
            var text = input?.Trim().Trim('"') ?? "";
            if (text == "")
            {
                error = "Boş değer.";
                return false;
            }

            if (text.StartsWith("U2FsdGVkX1"))   // base64("Salted__")
            {
                try
                {
                    var json = DecryptCryptoJs(text, WebStoragePassphrase);
                    text = JsonSerializer.Deserialize<string>(json) ?? "";
                }
                catch (Exception)
                {
                    error = "token-v-mhrs değeri çözülemedi (eksik/yanlış kopyalanmış olabilir).";
                    return false;
                }
            }

            if (text.Split('.').Length != 3 || !text.StartsWith("eyJ"))
            {
                error = "Geçerli bir JWT değil. eyJ... ile başlayan değeri ya da token-v-mhrs değerini (U2FsdGVkX1...) gönderin.";
                return false;
            }
            var exp = JwtTokenUtil.GetTokenExpireTime(text);
            if (exp == DateTime.MinValue)
            {
                error = "JWT okunamadı.";
                return false;
            }
            if (exp <= now)
            {
                error = $"Bu oturumun süresi dolmuş ({exp:dd.MM HH:mm}). mhrs.gov.tr'ye yeniden girip yeni değeri gönderin.";
                return false;
            }
            jwt = text;
            return true;
        }

        /// <summary>CryptoJS.AES.encrypt(metin, parola) çıktısını çözer (OpenSSL EVP_BytesToKey/MD5, AES-256-CBC).</summary>
        internal static string DecryptCryptoJs(string base64, string passphrase)
        {
            var data = Convert.FromBase64String(base64);
            if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, 8) != "Salted__")
                throw new FormatException("Salted__ başlığı yok");
            var salt = data.AsSpan(8, 8).ToArray();
            var cipher = data.AsSpan(16).ToArray();

            // EVP_BytesToKey (MD5, 1 tur): 32 bayt anahtar + 16 bayt IV
            var pass = Encoding.UTF8.GetBytes(passphrase);
            var keyIv = new List<byte>();
            byte[] prev = Array.Empty<byte>();
            while (keyIv.Count < 48)
            {
                prev = MD5.HashData(prev.Concat(pass).Concat(salt).ToArray());
                keyIv.AddRange(prev);
            }
            using var aes = Aes.Create();
            aes.Key = keyIv.Take(32).ToArray();
            aes.IV = keyIv.Skip(32).Take(16).ToArray();
            return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, aes.IV, PaddingMode.PKCS7));
        }
    }
}
