using System.Text.Json;
using MHRS_OtomatikRandevu;
using MHRS_OtomatikRandevu.Models.ResponseModels;
using Xunit;

namespace MHRS_OtomatikRandevu.Tests;

/// <summary>
/// MHRS slot-sorgulama yanıtının biçimini taklit eden (kişisel veri içermeyen) örneklerle
/// en erken boş slotun doğru seçildiğini doğrular.
/// </summary>
public class SlotSelectionTests
{
    static readonly DateTime Now = new(2026, 11, 17, 20, 0, 0);

    // hekim -> muayene yeri -> gün (bos) -> slot (bos) -> subslot
    static string B(bool v) => v ? "true" : "false";

    static string Slot(long id, string start, bool bos = true) =>
        "{\"bos\":" + B(bos) + ",\"slot\":{\"id\":" + id + ",\"fkCetvelId\":1,\"muayeneYeriId\":10," +
        "\"baslangicZamani\":\"" + start + "\",\"bitisZamani\":\"" + start + "\",\"kalanKullanim\":1}}";

    static string Day(bool bos, params string[] slots) =>
        "{\"bos\":" + B(bos) + ",\"slotList\":[" + string.Join(",", slots) + "]}";

    static List<SlotResponseModel> Parse(params string[][] doctorsDays)
    {
        var doctors = doctorsDays.Select(days =>
            "{\"muayeneYeriSlotList\":[{\"saatSlotList\":[" + string.Join(",", days) + "]}]}");
        var json = "[{\"hekimSlotList\":[" + string.Join(",", doctors) + "]}]";
        return JsonSerializer.Deserialize<List<SlotResponseModel>>(json)!;
    }

    [Fact]
    public void Ilk_hekim_degil_tum_hekimler_arasindan_en_erken_secilir()
    {
        var data = Parse(
            new[] { Day(true, Slot(1, "2026-11-20 10:00:00")) },   // 1. hekim: geç
            new[] { Day(true, Slot(2, "2026-11-18 09:00:00")) });  // 2. hekim: erken

        var slot = Program.SelectEarliestSlot(data, DateTime.MinValue, DateTime.MaxValue, Now);

        Assert.Equal(2, slot!.Id);
    }

    [Fact]
    public void Dolu_slotlar_ve_dolu_gunler_atlanir()
    {
        var data = Parse(new[]
        {
            Day(false, Slot(1, "2026-11-18 08:00:00")),                       // gün dolu
            Day(true, Slot(2, "2026-11-18 09:00:00", bos: false),              // slot dolu
                      Slot(3, "2026-11-18 11:00:00")),
        });

        var slot = Program.SelectEarliestSlot(data, DateTime.MinValue, DateTime.MaxValue, Now);

        Assert.Equal(3, slot!.Id);
    }

    [Fact]
    public void Tarih_araligi_disindaki_slot_alinmaz()
    {
        var data = Parse(new[]
        {
            Day(true, Slot(1, "2026-11-19 09:00:00")),   // aralık dışı (ertesi gün)
            Day(true, Slot(2, "2026-11-18 15:30:00")),   // aralık içi
        });
        var min = new DateTime(2026, 11, 18);
        var max = new DateTime(2026, 11, 18, 23, 59, 59);

        var slot = Program.SelectEarliestSlot(data, min, max, Now);

        Assert.Equal(2, slot!.Id);
    }

    [Fact]
    public void Sadece_aralik_disi_slot_varsa_null_doner()
    {
        var data = Parse(new[] { Day(true, Slot(1, "2026-11-25 09:00:00")) });
        var min = new DateTime(2026, 11, 18);
        var max = new DateTime(2026, 11, 18, 23, 59, 59);

        Assert.Null(Program.SelectEarliestSlot(data, min, max, Now));
    }

    [Fact]
    public void Saati_gecmis_slot_alinmaz()
    {
        var data = Parse(new[]
        {
            Day(true, Slot(1, "2026-11-17 14:00:00")),   // bugün ama geçmiş saat
            Day(true, Slot(2, "2026-11-17 21:00:00")),
        });

        var slot = Program.SelectEarliestSlot(data, DateTime.MinValue, DateTime.MaxValue, Now);

        Assert.Equal(2, slot!.Id);
    }

    [Fact]
    public void Bos_veya_eksik_veride_null_doner()
    {
        Assert.Null(Program.SelectEarliestSlot(null, DateTime.MinValue, DateTime.MaxValue, Now));
        Assert.Null(Program.SelectEarliestSlot(new List<SlotResponseModel>(), DateTime.MinValue, DateTime.MaxValue, Now));
        var eksik = JsonSerializer.Deserialize<List<SlotResponseModel>>("""[{"hekimSlotList":null},{"hekimSlotList":[{"muayeneYeriSlotList":null}]}]""");
        Assert.Null(Program.SelectEarliestSlot(eksik, DateTime.MinValue, DateTime.MaxValue, Now));
    }

    [Fact]
    public void Okunamayan_tarihli_slot_atlanir()
    {
        var data = Parse(new[] { Day(true, Slot(1, "bozuk"), Slot(2, "2026-11-18 10:00:00")) });

        var slot = Program.SelectEarliestSlot(data, DateTime.MinValue, DateTime.MaxValue, Now);

        Assert.Equal(2, slot!.Id);
    }
}
