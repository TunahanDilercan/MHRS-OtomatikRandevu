using MHRS_OtomatikRandevu;
using Xunit;

namespace MHRS_OtomatikRandevu.Tests;

public class BotConfigTests
{
    static readonly DateTime Today = new(2026, 11, 17);

    static (BotConfig c, List<string> errors) Load(params (string k, string v)[] extra)
    {
        var env = new Dictionary<string, string>
        {
            ["MHRS_TC"] = "11111111111",
            ["MHRS_PASSWORD"] = "x",
            ["MHRS_PROVINCE_ID"] = "42",
            ["MHRS_DISTRICT_ID"] = "-1",
            ["MHRS_CLINIC_ID"] = "144",
        };
        foreach (var (k, v) in extra) env[k] = v;
        return BotConfig.From(k => env.TryGetValue(k, out var v) ? v : null, Today);
    }

    [Fact]
    public void Gecerli_minimum_ayar_hatasiz_ve_varsayilanlar_dogru()
    {
        var (c, errors) = Load();

        Assert.Empty(errors);
        Assert.Equal(42, c.ProvinceId);
        Assert.Equal(-1, c.DistrictId);
        Assert.Equal(144, c.ClinicId);
        Assert.Equal(BotConfig.Any, c.HospitalId);   // boş = FARKETMEZ
        Assert.Equal(new[] { BotConfig.Any }, c.DoctorIds);
        Assert.Equal(Today, c.StartDate);
        Assert.Null(c.EndDate);
        Assert.False(c.Continuous);
        Assert.Equal(120, c.IntervalSeconds);
        Assert.Equal(10, c.NotifyFrequency);
    }

    [Fact]
    public void Tek_gun_modu_bitis_gunu_dahil()
    {
        var (c, errors) = Load(("MHRS_START_DATE", "18-11-2026"), ("MHRS_END_DATE", "18-11-2026"), ("MHRS_CONTINUOUS", "1"));

        Assert.Empty(errors);
        Assert.Equal(new DateTime(2026, 11, 18), c.StartDate);
        Assert.Equal(new DateTime(2026, 11, 18, 23, 59, 59), c.EndDate);
        Assert.True(c.Continuous);
    }

    [Fact]
    public void Sifir_FARKETMEZ_sayilir()
    {
        var (c, _) = Load(("MHRS_DISTRICT_ID", "0"), ("MHRS_HOSPITAL_ID", "0"), ("MHRS_DOCTOR_ID", "0"));
        Assert.Equal(-1, c.DistrictId);
        Assert.Equal(-1, c.HospitalId);
        Assert.Equal(new[] { -1 }, c.DoctorIds);
    }

    [Fact]
    public void Tum_hatalar_tek_seferde_toplanir()
    {
        var (_, errors) = Load(
            ("MHRS_PASSWORD", ""),
            ("MHRS_CLINIC_ID", "gastro"),
            ("MHRS_START_DATE", "2026-11-18"),
            ("MHRS_SCHEDULE_TIMES", "09:55,9:55"));

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("MHRS_PASSWORD"));
        Assert.Contains(errors, e => e.Contains("MHRS_CLINIC_ID"));
        Assert.Contains(errors, e => e.Contains("MHRS_START_DATE"));
        Assert.Contains(errors, e => e.Contains("9:55"));   // "09:55" olmalıydı; eskiden sessizce hiç çalışmıyordu
    }

    [Fact]
    public void Bitis_baslangictan_once_ise_hata()
    {
        var (_, errors) = Load(("MHRS_START_DATE", "20-11-2026"), ("MHRS_END_DATE", "18-11-2026"));
        Assert.Single(errors);
    }

    [Fact]
    public void Il_eksikse_hata()
    {
        var (_, errors) = Load(("MHRS_PROVINCE_ID", ""));
        Assert.Contains(errors, e => e.Contains("MHRS_PROVINCE_ID"));
    }

    [Theory]
    [InlineData("0")]     // eskiden attemptCount % 0 ile çöküyordu
    [InlineData("-5")]
    [InlineData("abc")]
    public void Gecersiz_bildirim_sikligi_uyari_verir_varsayilan_kalir(string v)
    {
        var (c, errors) = Load(("TELEGRAM_NOTIFY_FREQUENCY", v));
        Assert.Empty(errors);
        Assert.Equal(10, c.NotifyFrequency);
        Assert.Single(c.Warnings);
    }

    [Fact]
    public void Kucuk_aralik_uyari_verir()
    {
        var (c, _) = Load(("MHRS_INTERVAL_SECONDS", "5"));
        Assert.Equal(120, c.IntervalSeconds);
        Assert.Single(c.Warnings);
    }
}

public class DenemeModuTests
{
    static BotConfig Load(string? dry)
    {
        var env = new Dictionary<string, string?>
        {
            ["MHRS_TC"] = "1", ["MHRS_PASSWORD"] = "x", ["MHRS_PROVINCE_ID"] = "42",
            ["MHRS_DISTRICT_ID"] = "-1", ["MHRS_CLINIC_ID"] = "144", ["MHRS_DRY_RUN"] = dry,
        };
        return BotConfig.From(k => env.TryGetValue(k, out var v) ? v : null, new DateTime(2026, 11, 19)).Config;
    }

    [Fact]
    public void Varsayilan_gercek_mod()
    {
        Assert.False(Load(null).DryRun);
        Assert.False(Load("0").DryRun);
    }

    [Fact]
    public void Deneme_modu_acilir()
    {
        Assert.True(Load("1").DryRun);
    }
}
