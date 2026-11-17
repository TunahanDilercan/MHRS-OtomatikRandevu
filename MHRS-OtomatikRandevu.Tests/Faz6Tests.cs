using MHRS_OtomatikRandevu;
using MHRS_OtomatikRandevu.Models.ResponseModels;
using Xunit;

namespace MHRS_OtomatikRandevu.Tests;

public class KomutTests
{
    [Theory]
    [InlineData("/durum", "durum", 0)]
    [InlineData("/tarih@MhrsBot 18-11-2026", "tarih", 1)]   // grup sohbetinde bot adıyla
    [InlineData("  /TARIH 18-11-2026 20-11-2026 ", "tarih", 2)]
    public void Komut_ve_argumanlar_ayristirilir(string text, string cmd, int argCount)
    {
        var p = Program.ParseCommand(text);
        Assert.NotNull(p);
        Assert.Equal(cmd, p!.Value.Command);
        Assert.Equal(argCount, p.Value.Args.Length);
    }

    [Theory]
    [InlineData("merhaba")]
    [InlineData("")]
    [InlineData(null)]
    public void Komut_olmayan_metin_null(string? text)
    {
        Assert.Null(Program.ParseCommand(text));
    }

    static readonly DateTime Today = new(2026, 11, 17);

    [Fact]
    public void Tek_tarih_sadece_o_gun()
    {
        Assert.True(Program.TryParseDateRangeArgs(new[] { "18-11-2026" }, Today, out var s, out var e, out _));
        Assert.Equal(new DateTime(2026, 11, 18), s);
        Assert.Equal(new DateTime(2026, 11, 18, 23, 59, 59), e);
    }

    [Fact]
    public void Iki_tarih_aralik_bitis_dahil()
    {
        Assert.True(Program.TryParseDateRangeArgs(new[] { "18-11-2026", "20-11-2026" }, Today, out var s, out var e, out _));
        Assert.Equal(new DateTime(2026, 11, 18), s);
        Assert.Equal(new DateTime(2026, 11, 20, 23, 59, 59), e);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-11-18")]
    [InlineData("20-11-2026 18-11-2026")]              // bitiş başlangıçtan önce
    [InlineData("10-11-2026")]                         // geçmiş
    [InlineData("18-11-2026 19-11-2026 20-11-2026")]
    public void Gecersiz_tarih_argumanlari_reddedilir(string argsText)
    {
        var args = argsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.False(Program.TryParseDateRangeArgs(args, Today, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Bilinmeyen_komut_yardim_doner()
    {
        Assert.Contains("/durum", Program.HandleCommand("/bilinmeyen"));
    }
}

public class CokluDoktorTests
{
    static Program.SlotSearchResult Found(string start) =>
        new(new SubSlot { Id = start.GetHashCode(), BaslangicZamani = start });

    [Fact]
    public void Doktorlar_arasindan_en_erken_slot_secilir()
    {
        var r = Program.CombineResults(new[]
        {
            Found("2026-11-20 10:00:00"),
            new Program.SlotSearchResult(null),                 // bu doktorda yok
            Found("2026-11-18 09:30:00"),
        });
        Assert.Equal("2026-11-18 09:30:00", r.Slot!.BaslangicZamani);
    }

    [Fact]
    public void Bir_doktor_hata_digeri_yok_ise_hata_sayilmaz()
    {
        var r = Program.CombineResults(new[]
        {
            new Program.SlotSearchResult(null, true, "BAGLANTI", "x"),
            new Program.SlotSearchResult(null),
        });
        Assert.False(r.IsError);
        Assert.Null(r.Slot);
    }

    [Fact]
    public void Hepsi_hata_ise_hata()
    {
        var r = Program.CombineResults(new[]
        {
            new Program.SlotSearchResult(null, true, "E1", "a"),
            new Program.SlotSearchResult(null, true, "E2", "b"),
        });
        Assert.True(r.IsError);
        Assert.Equal("E1", r.ErrorCode);
    }

    static (BotConfig c, List<string> e) Load(string doctors)
    {
        var env = new Dictionary<string, string>
        {
            ["MHRS_TC"] = "1", ["MHRS_PASSWORD"] = "x", ["MHRS_PROVINCE_ID"] = "42",
            ["MHRS_DISTRICT_ID"] = "-1", ["MHRS_CLINIC_ID"] = "144", ["MHRS_DOCTOR_ID"] = doctors,
        };
        return BotConfig.From(k => env.TryGetValue(k, out var v) ? v : null, new DateTime(2026, 11, 17));
    }

    [Fact]
    public void Virgullu_doktor_listesi_okunur()
    {
        var (c, e) = Load("34448, 2783,34448");
        Assert.Empty(e);
        Assert.Equal(new[] { 34448, 2783 }, c.DoctorIds);
    }

    [Fact]
    public void Farketmez_baska_idlerle_birlikte_olamaz()
    {
        var (_, e) = Load("-1,34448");
        Assert.Single(e);
    }

    [Fact]
    public void Bos_doktor_farketmez()
    {
        var (c, _) = Load("");
        Assert.Equal(new[] { -1 }, c.DoctorIds);
    }
}
