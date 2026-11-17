using System.Text.Json;
using MHRS_OtomatikRandevu;
using MHRS_OtomatikRandevu.Utils;
using Xunit;

namespace MHRS_OtomatikRandevu.Tests;

public class TarihTests
{
    [Theory]
    [InlineData("18-11-2026", 2026, 11, 18)]
    [InlineData("8-1-2026", 2026, 1, 8)]
    [InlineData(" 01-02-2027 ", 2027, 2, 1)]
    public void Gecerli_GG_AA_YYYY_okunur(string text, int y, int m, int d)
    {
        Assert.True(Program.TryParseTrDate(text, out var date));
        Assert.Equal(new DateTime(y, m, d), date);
    }

    [Theory]
    [InlineData("2026-11-18")]   // ISO biçimi kabul edilmez
    [InlineData("31-02-2026")]   // olmayan gün
    [InlineData("18/11/2026")]
    [InlineData("")]
    [InlineData(null)]
    public void Gecersiz_tarih_reddedilir(string? text)
    {
        Assert.False(Program.TryParseTrDate(text, out _));
    }
}

public class ApiErrorUtilTests
{
    static object[] Errors(string json) =>
        JsonSerializer.Deserialize<object[]>(json)!;

    [Fact]
    public void Ilk_hatanin_kodu_ve_mesaji_okunur()
    {
        var (kod, mesaj) = ApiErrorUtil.First(Errors("""[{"kodu":"VT2003","mesaj":"T.C. Kimlik Numarası ve/veya parola bilgisi hatalıdır."}]"""));
        Assert.Equal("VT2003", kod);
        Assert.StartsWith("T.C. Kimlik", mesaj);
    }

    [Fact]
    public void Hata_yoksa_null()
    {
        Assert.Equal((null, null), ApiErrorUtil.First(null));
        Assert.Equal((null, null), ApiErrorUtil.First(Array.Empty<object>()));
    }

    [Fact]
    public void Kod_iceriyor_mu()
    {
        var e = Errors("""[{"kodu":"RND4010","mesaj":"Randevu bulunamadı"}]""");
        Assert.True(ApiErrorUtil.Contains(e, "RND4010"));
        Assert.False(ApiErrorUtil.Contains(e, "LGN2001"));
        Assert.False(ApiErrorUtil.Contains(null, "RND4010"));
    }

    [Theory]
    [InlineData("VT2003", true)]    // yanlış TC/şifre
    [InlineData("LGN1004", true)]
    [InlineData("LGN2001", false)]  // oturum düştü: yeniden giriş yapılmalı, kalıcı değil
    [InlineData("RND4010", false)]
    [InlineData(null, false)]       // bağlantı hatası: geçici
    public void Kalici_giris_hatasi_siniflandirmasi(string? kod, bool kalici)
    {
        Assert.Equal(kalici, ApiErrorUtil.IsPermanentLoginError(kod));
    }
}

public class SaatPenceresiTests
{
    [Theory]
    [InlineData(9, 55, true)]    // sabah açılışı
    [InlineData(10, 15, true)]
    [InlineData(19, 58, true)]   // akşam iptalleri
    [InlineData(14, 59, true)]   // saat başı (57-04)
    [InlineData(15, 2, true)]
    [InlineData(0, 3, true)]     // gece
    public void Bilinen_pencereler_aciktir(int h, int m, bool beklenen)
    {
        Assert.Equal(beklenen, Program.IsWithinAllowedWindow(new DateTime(2026, 11, 17, h, m, 0)));
    }

    [Fact]
    public void Pencere_disinda_cogunlukla_kapalidir()
    {
        // 05-14 dakikaları hiçbir pencereye girmez (rastgele gruplar 15-47 arası)
        for (int h = 3; h < 9; h++)
            for (int m = 5; m <= 14; m++)
                Assert.False(Program.IsWithinAllowedWindow(new DateTime(2026, 11, 17, h, m, 0)), $"{h:00}:{m:00}");
    }
}
