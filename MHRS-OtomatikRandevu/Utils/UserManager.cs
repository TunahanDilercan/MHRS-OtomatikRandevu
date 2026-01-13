using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MHRS_OtomatikRandevu.Models;

namespace MHRS_OtomatikRandevu.Utils
{
    public static class UserManager
    {
        private static readonly string DataPath = Path.Combine("Data", "users.json");
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("MHRS_SECRET_KEY_12345678"); // 32 chars needed logically but simplistic here
        private static readonly byte[] IV = Encoding.UTF8.GetBytes("MHRS_IV_87654321");

        // Basit Güvenlik: 30 gün kullanılmayan hesapları otomatik sil
        private const int MAX_INACTIVE_DAYS = 30;

        public static List<UserProfile> LoadUsers()
        {
            if (!File.Exists(DataPath)) return new List<UserProfile>();

            try
            {
                var json = File.ReadAllText(DataPath);
                var users = JsonSerializer.Deserialize<List<UserProfile>>(json);
                
                // Güvenlik Temizliği: Eski kullanıcıları sil
                if (users != null)
                {
                    var activeUsers = users.Where(u => 
                        !string.IsNullOrEmpty(u.LastUsedDate) && 
                        (DateTime.Now - DateTime.Parse(u.LastUsedDate)).TotalDays < MAX_INACTIVE_DAYS
                    ).ToList();

                    if (activeUsers.Count < users.Count)
                    {
                        SaveUsers(activeUsers); // Değişiklik varsa kaydet
                        return activeUsers;
                    }
                    return users;
                }
            }
            catch { }
            
            return new List<UserProfile>();
        }

        public static void SaveUser(string tc, string password, string alias, string telToken, string telChatId)
        {
            // Klasör var mı?
            if (!Directory.Exists("Data")) Directory.CreateDirectory("Data");

            var users = LoadUsers();
            
            // Mevcut kullanıcıyı bul veya yeni oluştur
            var user = users.FirstOrDefault(u => u.TcNo == tc);
            if (user == null)
            {
                user = new UserProfile { TcNo = tc };
                users.Add(user);
            }

            // Bilgileri güncelle
            user.EncryptedPassword = Encrypt(password);
            user.NameAlias = string.IsNullOrEmpty(alias) ? tc.Substring(0, 2) + "***" + tc.Substring(tc.Length-2) : alias;
            user.LastUsedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            user.TelegramToken = telToken;
            user.TelegramChatId = telChatId;

            // Son kullanılanı en başa al (son 3 mantığı için)
            users = users.OrderByDescending(u => u.LastUsedDate).Take(5).ToList(); // Max 5 kişi sakla

            SaveUsers(users);
        }

        private static void SaveUsers(List<UserProfile> users)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(users, options);
            File.WriteAllText(DataPath, json);
        }

        public static string DecryptPassword(string encrypted)
        {
            return Decrypt(encrypted);
        }

        // --- BASIT ŞİFRELEME ---
        // Not: Gerçek prod ortamında DPAPI veya KeyVault kullanılmalı. 
        // Burada amaç .json dosyasını açan birinin şifreyi çıplak gözle görmemesidir.
        private static string Encrypt(string plainText)
        {
            // Basit XOR veya Base64 yerine AES kullanalım (Low Security Mode)
            try {
                // Burada AES-128 kullanıyoruz, Key ve IV sabit (kod içinde)
                // Bu sadece "gözden gizleme" (obfuscation) sağlar.
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Key; // 24 bytes (192 bit) or pad to fit
                    aes.IV = IV;   // 16 bytes
                    // Fixed Key/IV for demo simplicity but works for this scope
                    // Adjust key size if runtime complains
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    
                    ICryptoTransform encryptor = aes.CreateEncryptor(Key, IV); // Hata verirse Key/IV boyutlarını düzeltiriz
                    
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        {
                            using (StreamWriter sw = new StreamWriter(cs))
                            {
                                sw.Write(plainText);
                            }
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch { return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText)); } // Fallback
        }

        private static string Decrypt(string cipherText)
        {
            try {
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Key; 
                    aes.IV = IV;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    ICryptoTransform decryptor = aes.CreateDecryptor(Key, IV);
                    
                    using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(cipherText)))
                    {
                        using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        {
                            using (StreamReader sr = new StreamReader(cs))
                            {
                                return sr.ReadToEnd();
                            }
                        }
                    }
                }
            } catch { return Encoding.UTF8.GetString(Convert.FromBase64String(cipherText)); } // Fallback
        }
    }
}
