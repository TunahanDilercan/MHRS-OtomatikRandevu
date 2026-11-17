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
    // Giriş, token önbelleği ve giriş hatası işleme
    public partial class Program
    {
        static void ApplyToken(JwtTokenModel token)
        {
            JWT_TOKEN = token.Token;
            TOKEN_END_DATE = token.Expiration;
            _client!.AddOrUpdateAuthorizationHeader(JWT_TOKEN);
        }

        /// <summary>
        /// İşlemi çalıştırır; MHRS oturumu düşmüşse (LGN2001) bir kez yeniden giriş yapıp tekrar dener.
        /// Yeniden giriş başarısızsa ExitForLoginFailure ile süreç sonlanır. Yeniden girişten sonra
        /// oturum yine düşerse SessionExpiredException çağırana iletilir (çoklu giriş vb.).
        /// Önceden aynı kurtarma kodu 6 yerde kopyalanmıştı.
        /// </summary>
        static T CallWithSessionRecovery<T>(Func<T> action, string context, bool notifyRecovery = false)
        {
            try
            {
                return action();
            }
            catch (SessionExpiredException ex)
            {
                Console.WriteLine($"[WARNING] 🔄 Oturum düştü ({context}): {ex.Message}");
                LogStatus($"Oturum düştü ({context}), yeniden giriş deneniyor", null, true);

                var newToken = ForceLogin(_client!);
                if (newToken == null || string.IsNullOrEmpty(newToken.Token))
                {
                    LogStatus($"Oturum kurtarma başarısız ({context})", null, true);
                    ExitForLoginFailure($"oturum kurtarma: {context}");
                    throw;   // ExitForLoginFailure süreci sonlandırır; buraya gelinmez
                }

                ApplyToken(newToken);
                Console.WriteLine($"[INFO] ✅ Oturum yenilendi, tekrar deneniyor ({context})");
                LogStatus($"Oturum yenilendi, tekrar deneniyor ({context})", null, true);

                if (notifyRecovery && _notificationService != null)
                {
                    var recoveryMessage = $"✅ Session Recovery Başarılı!\n\n🔄 MHRS oturumu otomatik yenilendi\n⏰ Saat: {DateTime.Now:HH:mm:ss}\n📍 {context}\n🎯 Randevu arama devam ediyor...";
                    _ = Task.Run(() => _notificationService.SendNotification(recoveryMessage));
                }

                return action();   // ikinci SessionExpiredException çağırana gider
            }
        }

        static JwtTokenModel? GetToken(IClientService client)
        {
            // Cross-platform token file path
            var tokenFilePath = Path.Combine(Directory.GetCurrentDirectory(), TOKEN_FILE_NAME);

            try
            {
                var tokenData = File.ReadAllText(tokenFilePath);
                if (string.IsNullOrEmpty(tokenData) || JwtTokenUtil.GetTokenExpireTime(tokenData) < DateTime.Now)
                    throw new Exception();

                return new() { Token = tokenData, Expiration = JwtTokenUtil.GetTokenExpireTime(tokenData) };
            }
            catch (Exception)
            {
                var loginRequestModel = new LoginRequestModel
                {
                    KullaniciAdi = TC_NO,
                    Parola = SIFRE
                };

                var loginResponse = client.Post<LoginResponseModel>(MHRSUrls.BaseUrl, MHRSUrls.Login, loginRequestModel).Result;
                if (loginResponse.Data == null || string.IsNullOrEmpty(loginResponse.Data?.Jwt))
                {
                    LAST_LOGIN_ERROR = ApiErrorUtil.First(loginResponse.Errors);
                    Console.WriteLine("Giriş yapılırken bir hata meydana geldi!");
                    return null;
                }

                if (!string.IsNullOrEmpty(tokenFilePath))
                    File.WriteAllText(tokenFilePath, loginResponse.Data!.Jwt);

                return new() { Token = loginResponse.Data!.Jwt, Expiration = JwtTokenUtil.GetTokenExpireTime(loginResponse.Data!.Jwt) };
            }
        }

        /// <summary>
        /// Forces a new login by clearing the cached token and re-authenticating
        /// </summary>
        static JwtTokenModel? ForceLogin(IClientService client)
        {
            Console.WriteLine("[INFO] 🔄 Session recovery: Forcing fresh login...");
            LogStatus("Session recovery: Forcing fresh login", null, true);
            
            // Clear cached token file to force fresh login (cross-platform path)
            var tokenFilePath = Path.Combine(Directory.GetCurrentDirectory(), TOKEN_FILE_NAME);
            
            // Delete existing token file
            try
            {
                if (File.Exists(tokenFilePath))
                {
                    File.Delete(tokenFilePath);
                    Console.WriteLine("[INFO] 🗑️  Cleared cached token");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Could not delete token file: {ex.Message}");
            }
            
            // Clear authorization header to avoid sending expired token
            client.ClearAuthorizationHeader();
            Console.WriteLine("[INFO] 🔓 Cleared authorization header");
            
            // Perform fresh login
            var loginRequestModel = new LoginRequestModel
            {
                KullaniciAdi = TC_NO,
                Parola = SIFRE
            };

            try
            {
                var loginResponse = client.Post<LoginResponseModel>(MHRSUrls.BaseUrl, MHRSUrls.Login, loginRequestModel).Result;
                if (loginResponse.Data == null || string.IsNullOrEmpty(loginResponse.Data?.Jwt))
                {
                    LAST_LOGIN_ERROR = ApiErrorUtil.First(loginResponse.Errors);
                    Console.WriteLine("[ERROR] ❌ Session recovery failed: Could not obtain new token");
                    LogStatus("Session recovery failed: Could not obtain new token", null, true);
                    return null;
                }

                // Save new token
                if (!string.IsNullOrEmpty(tokenFilePath))
                    File.WriteAllText(tokenFilePath, loginResponse.Data!.Jwt);

                Console.WriteLine("[INFO] ✅ Session recovery successful: New token obtained");
                LogStatus("Session recovery successful: New token obtained", null, true);
                
                return new() { Token = loginResponse.Data!.Jwt, Expiration = JwtTokenUtil.GetTokenExpireTime(loginResponse.Data!.Jwt) };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] ❌ Session recovery failed with exception: {ex.Message}");
                LogStatus($"Session recovery failed with exception: {ex.Message}", null, true);
                return null;
            }
        }

        static (string? Kod, string? Mesaj) LAST_LOGIN_ERROR;

        /// <summary>
        /// Giriş başarısızsa: yanlış şifre gibi kalıcı hatalarda kod 2 ile çık (systemd yeniden
        /// başlatmaz, hatalı giriş sayısı artmaz, hesap kilitlenmez); geçici hatalarda kod 1.
        /// </summary>
        static void ExitForLoginFailure(string context)
        {
            var (kod, mesaj) = LAST_LOGIN_ERROR;
            if (ApiErrorUtil.IsPermanentLoginError(kod))
                ExitWith(2, $"❌ MHRS girişi reddedildi ({context}).\nKod: {kod}\n{mesaj}\n\nBot durduruldu ve yeniden denemeyecek (hesap kilitlenmesin). .env'deki TC/şifreyi düzeltip servisi yeniden başlatın.", notify: true);
            else
                ExitWith(1, $"MHRS girişi geçici olarak başarısız ({context}): {kod ?? "bağlantı"} {mesaj}", notify: false);
        }
    }
}
