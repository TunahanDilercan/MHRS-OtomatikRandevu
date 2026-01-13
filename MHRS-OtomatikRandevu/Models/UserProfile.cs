
namespace MHRS_OtomatikRandevu.Models
{
    public class UserProfile
    {
        public string TcNo { get; set; } = "";
        public string EncryptedPassword { get; set; } = ""; // Basit bir scramble yapılacak
        public string LastUsedDate { get; set; } = ""; // YYYY-MM-DD HH:mm
        public string NameAlias { get; set; } = ""; // Kullanıcı Tanımı (Örn: Annem, Babam)
        public string TelegramToken { get; set; } = ""; // Kişiye özel bot token
        public string TelegramChatId { get; set; } = "";
    }
}
