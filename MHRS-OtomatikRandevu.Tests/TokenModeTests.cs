using MHRS_OtomatikRandevu;
using MHRS_OtomatikRandevu.Utils;
using Xunit;

namespace MHRS_OtomatikRandevu.Tests;

public class TokenModeTests
{
    // exp = 1900000000 (2030-03-17). İmza sahte; bot imzayı doğrulamaz, MHRS doğrular.
    const string Jwt = "eyJhbGciOiJIUzUxMiJ9.eyJzdWIiOiJ0ZXN0IiwiZXhwIjoxOTAwMDAwMDAwfQ.c2ln";

    // Bağımsız üretildi: printf '"<Jwt>"' | openssl enc -aes-256-cbc -md md5 -salt -pass pass:mhrs-secret-key -base64 -A
    // (mhrs.gov.tr'nin localStorage["token-v-mhrs"] için kullandığı CryptoJS.AES biçimiyle aynı)
    const string WebStorageValue = "U2FsdGVkX1/JcEbCBwszZgYmXsdTreEMtiroUR/+sw1XpSjmZKdSjs2PMfyLjOtqg0EjlTuGVvHaZq48oG8O4pns0U7mw38wKJT2H20pDCHJd53vG9PPGp4Hm2QNGgMe";

    static readonly DateTime Now = new(2026, 11, 17, 21, 0, 0);

    [Fact]
    public void Tarayicidaki_sifreli_deger_cozulur()
    {
        Assert.True(MhrsTokenUtil.TryExtractJwt(WebStorageValue, Now, out var jwt, out var err), err);
        Assert.Equal(Jwt, jwt);
    }

    [Theory]
    [InlineData(Jwt)]
    [InlineData("  \"" + Jwt + "\"  ")]   // tırnak ve boşlukla kopyalanmış
    public void Ham_jwt_kabul_edilir(string input)
    {
        Assert.True(MhrsTokenUtil.TryExtractJwt(input, Now, out var jwt, out _));
        Assert.Equal(Jwt, jwt);
    }

    [Fact]
    public void Suresi_dolmus_oturum_reddedilir()
    {
        Assert.False(MhrsTokenUtil.TryExtractJwt(Jwt, new DateTime(2031, 1, 1), out _, out var err));
        Assert.Contains("süresi dolmuş", err);
    }

    [Theory]
    [InlineData("")]
    [InlineData("merhaba")]
    [InlineData("U2FsdGVkX1/bozukbozukbozuk")]                 // eksik kopyalanmış şifreli değer
    [InlineData("eyJhbGciOiJIUzUxMiJ9.bozuk!!.c2ln")]
    public void Gecersiz_deger_reddedilir(string input)
    {
        Assert.False(MhrsTokenUtil.TryExtractJwt(input, Now, out _, out var err));
        Assert.NotEmpty(err);
    }

    static (BotConfig c, List<string> e) Load(params (string k, string v)[] extra)
    {
        var env = new Dictionary<string, string>
        {
            ["MHRS_PROVINCE_ID"] = "42", ["MHRS_DISTRICT_ID"] = "-1", ["MHRS_CLINIC_ID"] = "144",
        };
        foreach (var (k, v) in extra) env[k] = v;
        return BotConfig.From(k => env.TryGetValue(k, out var v) ? v : null, new DateTime(2026, 11, 17));
    }

    [Fact]
    public void Token_modunda_TC_sifre_gerekmez()
    {
        var (c, e) = Load(("MHRS_AUTH_MODE", "token"));
        Assert.Empty(e);
        Assert.True(c.TokenMode);
    }

    [Fact]
    public void Sifre_modunda_TC_sifre_gerekir()
    {
        var (c, e) = Load();
        Assert.False(c.TokenMode);
        Assert.Contains(e, x => x.Contains("MHRS_PASSWORD"));
    }

    [Fact]
    public void Bilinmeyen_giris_modu_hata()
    {
        var (_, e) = Load(("MHRS_AUTH_MODE", "enabiz"), ("MHRS_TC", "1"), ("MHRS_PASSWORD", "x"));
        Assert.Contains(e, x => x.Contains("MHRS_AUTH_MODE"));
    }

    [Fact]
    public void Sifre_modunda_token_komutu_reddedilir()
    {
        Assert.Contains("MHRS_AUTH_MODE=token", Program.HandleCommand("/token " + Jwt));
    }
}
