# MHRS Otomatik Randevu Botu - Sunucu Sürümü

Bu proje, orijinal Windows uygulamasından forklanarak **Linux (Ubuntu/Debian)** sunucularda "headless" (arayüzsüz) hizmet olarak çalışacak şekilde yeniden geliştirilmiştir.

Sunucular için optimize edilmiş servis yapısı, otomatik kurulum scripti, merkezi yönetim paneli ve Telegram bildirim desteği sunar.

---

## Kurulum ve Başlangıç

Proje dosyalarını sunucuya yükledikten sonra terminal üzerinden şu adımları izleyin:

### 1. Yetkilendirme
Scriptlerin çalışabilmesi için izin verin:
```bash
chmod +x *.sh
```

### 2. İlk Kurulum
Gerekli paketleri (.NET 10 SDK) kurmak ve projeyi hazırlamak için:
```bash
./setup.sh
```
*Bu işlem bitince otomatik olarak kurulum sihirbazı açılacak ve gerekli ayarları (TC, Şifre, İl, Hastane vb.) yapmanızı isteyecektir.*

### 3. Telegram Bildirim Ayarları
Randevu alındığında anında bildirim almak için Telegram botu kurmanız önerilir:

1.  Telegram'da **@BotFather** kullanıcısını bulun ve `/newbot` komutunu gönderin.
2.  Botunuza bir isim verin ve size vereceği **API Token**'ı kopyalayın.
3.  Kendi oluşturduğunuz bota bir mesaj atın (Merhaba vb.).
4.  Tarayıcıdan `https://api.telegram.org/bot<TOKEN>/getUpdates` adresine gidin.
5.  JSON sonucunun içinde `"chat":{"id":123456789...` kısmındaki **ID** numarasını alın.
6.  Bu bilgileri kurulum sihirbazında ilgili alana girin.

### 4. Tarih ve Arama Modu (`.env`)

| Ayar | Açıklama |
| :--- | :--- |
| `MHRS_START_DATE` | `GG-AA-YYYY`. Boşsa bugün. Geçersizse bot başlamaz. |
| `MHRS_END_DATE` | `GG-AA-YYYY`. Boşsa bugünden 12 gün. Verilirse **o günün sonuna kadarki** slotlar alınır ve tarih geçince bot Telegram'a haber verip **kendini durdurur**. |
| `MHRS_CONTINUOUS` | `1` = saat pencerelerini beklemeden sürekli ara (kısa tarih aralıkları için). Varsayılan `0`. |
| `MHRS_INTERVAL_SECONDS` | İki deneme arası bekleme (sn, en az 30). Varsayılan `120`. |

**Sadece tek bir gün için randevu:** `MHRS_START_DATE` ve `MHRS_END_DATE`'i aynı güne, `MHRS_CONTINUOUS=1` yapın.

Bulunan slot tarih aralığının dışındaysa veya saati geçmişse **alınmaz**.

### 5. Çıkış Kodları (servis davranışı)

| Kod | Anlamı | systemd |
| :--- | :--- | :--- |
| `0` | Randevu alındı | Yeniden başlatmaz |
| `1` | Geçici hata (bağlantı, MHRS bakımı) | 60 sn sonra yeniden başlatır |
| `2` | Kalıcı hata (yanlış şifre, geçersiz ayar), tarih aralığı bitti veya randevu zaten alınmış | **Yeniden başlatmaz** (hesap kilitlenmesin) |

---

## Yönetim Paneli

Botu başlatmak, durdurmak, logları görmek veya ayarları değiştirmek için **Manager** aracını kullanmanız yeterlidir.

```bash
sudo ./manager.sh
```

| İşlem | Açıklama |
| :--- | :--- |
| 🟢 **Başlat** / 🔴 **Durdur** | Servis durumunu yönetir. |
| 🔄 **Yeniden Başlat** | Botu hızlıca kapatıp açar. |
| 📋 **Log İzle** | Botun canlı işlem kayıtlarını gösterir. |
| 🏆 **Başarılı Randevular** | Alınan randevuları görüntüler ve yeni işlem için botu sıfırlar. |
| ⚙️ **Ayarları Düzenle** | Şehir, doktor veya zamanlama ayarlarını değiştirir. |
| 🚑 **Hata Onar / Sıfırla** | Giriş/Token hatası durumunda önbelleği temizleyerek botu resetler. |
| 🛠️ **Servis Kur** | Botun sunucu açıldığında otomatik başlamasını sağlar. |

---

## Başka Bir Bilgisayardan Sunucuya Yükleme (`deploy.sh`)

Sunucuya .NET kurmadan, yerelde derleyip tek dosya olarak göndermek için. Her hedef (ör. `konya`) sunucuda ayrı bir klasör (`/opt/mhrs-konya`) ve ayrı bir servis (`mhrs-konya`) olarak çalışır; aynı sunucudaki başka uygulamalara dokunulmaz.

```bash
export MHRS_SSH_HOST=sunucu-ip MHRS_SSH_PORT=22 MHRS_SSH_KEY=~/.ssh/anahtar
./deploy.sh konya --yeni   # ilk kez: klasör + servis oluşturur, sonra .env'i koyup başlatın
./deploy.sh konya          # güncelleme: test, derleme, yükleme, yeniden başlatma, sağlık kontrolü
```
Eski sürüm sunucuda `MHRS-OtomatikRandevu.eski` olarak saklanır.

## Geliştirme

```bash
dotnet test MHRS-OtomatikRandevu.Tests   # birim testleri (GitHub Actions'ta her PR'da da çalışır)
```

## Dosya Yapısı

*   `manager.sh`: Ana yönetim paneli.
*   `.env`: Yapılandırma ve ayar dosyası.
*   `randevu_log.txt`: İşlem kayıtları (Loglar). 5 MB'ı geçince `.1`–`.3` olarak döndürülür.
*   `randevu_basarili.txt`: Alınan son başarılı randevu bilgisi.

---

> [!CAUTION]
> ### YASAL UYARI VE SORUMLULUK REDDİ
> **Lütfen Dikkatle Okuyunuz:**
>
> Bu proje tamamen **EĞİTİM VE TEST AMAÇLI** geliştirilmiş açık kaynaklı bir yazılımdır. Temel amacı; HTTP istekleri, API güvenliği ve otomasyon mantığını incelemektir.
>
> 1.  **Sorumluluk:** Bu yazılımı indiren ve kullanan herkes, Türkiye Cumhuriyeti yasalarına (özellikle TCK Md. 243/244 Bilişim Suçları) uymakla yükümlüdür. Yazılımın kullanımından doğabilecek her türlü hukuki ve cezai sorumluluk **tamamen kullanıcıya aittir.**
> 2.  **Kötüye Kullanım:** Kamu hizmetlerini (MHRS vb.) engellemek veya sistemi yavaşlatmak amacıyla kullanılması kesinlikle yasaktır ve suç teşkil edebilir.
> 3.  **Geliştirici Beyanı:** Projeyi geliştirenler, kullanıcıların aracı kullanım şeklinden sorumlu tutulamaz. Yazılımı kullanarak bu şartları kabul etmiş sayılırsınız.
