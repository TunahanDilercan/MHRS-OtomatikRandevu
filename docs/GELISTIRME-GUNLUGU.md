# Geliştirme Günlüğü — Kasım 2026 İyileştirmeleri

Bu belge, Kasım 2026'da yapılan kapsamlı iyileştirmenin **ne yaptığını, neden yaptığını, neyin eklenip çıkarıldığını ve işlevsel olarak neyin değiştiğini** kaydeder. Sonraki çalışmaya buradan devam edilebilir.

> Kişisel/sunucuya özel bilgiler (sunucu adresi, TC, şifre, Telegram token) bu depoda **yoktur**; depo herkese açıktır.

---

## Özet

| | Önce | Sonra |
|---|---|---|
| .NET | 7 (desteği 2024'te bitti) | **10 LTS** |
| `Program.cs` | 1546 satır, tek dosya | 134 satır + 9 konu dosyası |
| Test | Yok (kopyalanmış deneme konsolu) | **81 birim testi**, her PR'da GitHub Actions'ta |
| Oturum düşerse | Bot fark etmiyor, ~20 saat kör kalıyordu | Otomatik yeniden giriş |
| Yanlış şifre | Servis 30 sn'de bir yeniden giriş deniyor → hesap kilidi riski | Telegram'a bildirir, durur |
| Yanlış hastane/doktor ID | Sessizce "FARKETMEZ" → ildeki herhangi bir hastaneden randevu | Geçerli seçenekleri yazar, durur |
| MHRS hatası | "Randevu yok" sanılıyordu | Ayrı sayılır, 5 ardışık hatada Telegram uyarısı |
| Tarih aralığı | Bitiş günü aramadan düşüyordu; geçmiş tarihte sonsuza kadar arıyordu | Bitiş günü dahil, tarih geçince durur |
| Doktor | Tek doktor; sadece ilk doktorun ilk muayene yerine bakıyordu | Tüm doktor/yer/günlerde en erken; virgülle birden fazla doktor |
| Telegram | Sadece bildirim | + komutlar (`/durum`, `/duraklat`, `/devam`, `/tarih`, `/hedef`, `/token`), sabah özeti |
| Giriş | Sadece TC + MHRS şifresi | + **token modu** (e-Nabız / e-Devlet oturumu) |
| Sunucuya kurulum | Sunucuda .NET SDK + derleme | `deploy.sh`: yerelde derle, tek dosya gönder |

---

## Fazlar

Her faz ayrı bir dalda yapıldı, derlendi, test edildi ve PR olarak `master`'a birleştirildi.

### Faz 0 — Acil yama (çalışan sunucu botu için)
**Neden:** Sunucuda tek günlük bir arama çalışıyordu; oturum düşerse bot kör kalacaktı.
- `ClientService.Post`: `SessionExpiredException` artık genel `catch` tarafından yutulmuyor.
- Randevu alındıktan sonra `Console.ReadKey()` yalnızca konsol varsa (systemd'de çöküyordu); başarı bildirimi gönderilmeden çıkılmıyor.
- Yalnızca sunucudaki eski sürüme uygulandı (bu sürüm yerel `yedek/eski-yerel` dalında).

### Faz 1 — Depo düzeni · PR #1
- Yerel kopya GitHub'dan farklı bir geçmişteydi → eski yerel geçmiş `yedek/eski-yerel` dalına alındı (GitHub'a gönderilmedi), `master` GitHub ile eşitlendi.
- `.gitignore` + `.user_history` (sihirbazın düz metin TC|şifre geçmişi), `Data/` (`users.json`), `.env.save*`, `.env.backup`.

### Faz 2 — Kritik hatalar · PR #2
- **Oturum düşmesi yutuluyordu** (`ClientService.Post` içindeki genel `catch`): bot "randevu yok" deyip token bitene kadar (~20 saat) kör kalıyordu. Düzeltildi.
- **Zaman aşımı** 5 dk → 30 sn.
- **Çıkış kodları:** `0` randevu alındı · `1` geçici hata (systemd yeniden başlatır) · `2` kalıcı hata / iş bitti (yeniden başlatmaz).
- **Yanlış şifre koruması:** `VT*` / `LGN*` (LGN2001 hariç) giriş hatalarında Telegram'a kodla bildirim + çıkış 2.
- **Hata ≠ randevu yok:** `RND4010` "yok", diğer her şey hata; 5 ardışık hatada ve sonra her 30'da uyarı, düzelince bilgi.
- **Randevu yanıtı:** HTTP 200'e ek olarak gövdedeki `success`/`errors` kontrolü.
- **`install_service.sh`:** `Restart=always` → `on-failure` + `RestartPreventExitStatus=2` + saatlik başlatma sınırı + `TZ=Europe/Istanbul`.
- `.gitattributes`: `.sh` dosyaları her ortamda LF.
- **Eklenen:** `Utils/ApiErrorUtil.cs`

### Faz 3 — Tarih modu · PR #3
- `MHRS_END_DATE` artık **o günün 23:59:59'una kadar** (önceden 00:00'dı, bitiş günü düşüyordu).
- Geçersiz tarih sessizce varsayılana düşmüyor → bot durur.
- Aralık dışındaki ve **saati geçmiş** slotlar alınmıyor (`SelectEarliestSlot`).
- Bitiş tarihi geçince Telegram'a haber + çıkış 2.
- `MHRS_CONTINUOUS=1`: saat pencerelerini beklemeden sürekli arama.
- `MHRS_INTERVAL_SECONDS` (varsayılan 120, en az 30).
- Sihirbaz: geçersiz başlangıç tarihi `.env`'e yazılmıyor; sürekli mod soruluyor.

### Faz 4 — Testler ve yeniden yapılandırma · PR #4
- **Önce testler:** `MHRS-OtomatikRandevu.Tests` (xUnit). Eski `test/` (kopyalanmış deneme konsolu) silindi.
- **Saf taşıma** (betikle satır satır doğrulandı) → `Program.Schedule.cs`, `Program.Auth.cs`, `Program.Slots.cs`, `Program.Output.cs`, `SetupWizard.cs`.
- **`BotConfig.cs`:** tüm ayarlar tek yerde okunup doğrulanıyor; hatalar **tek seferde** ve **MHRS'ye giriş yapmadan önce** listeleniyor.
- **`Program.Target.cs`** (`ResolveTarget`): il/ilçe/klinik/hastane/yer/doktor doğrulaması.
- **`Program.Search.cs`** (`RunSearchLoop`): arama döngüsü.
- **`CallWithSessionRecovery`:** 6 kez kopyalanmış oturum kurtarma bloğu tek yardımcıya indi.
- Bu sırada bulunan hatalar: `TELEGRAM_NOTIFY_FREQUENCY=0` sıfıra bölme ile çöküyordu; sayı alanına yazı → `int.Parse` çöküyordu; `MHRS_SCHEDULE_TIMES=9:55` hiç eşleşmiyordu; Telegram `parse_mode=HTML` `<`/`&` içeren mesajları reddediyordu (artık düz metin).

### Faz 5 — Platform · PR #5
- **.NET 7 → .NET 10 LTS.**
- **Çıkarılan paketler:** Twilio, System.Configuration.ConfigurationManager, Microsoft.Extensions.DependencyInjection (kodda kullanılmıyordu); JWT çözümü için kendi Base64Url fonksiyonu.
- **Log döndürme:** `randevu_log.txt` 5 MB'ı geçince `.1`–`.3`.
- **Eklenen:** `deploy.sh` (test → self-contained tek dosya → `/opt/mhrs-<hedef>` → yeniden başlat → sağlık kontrolü; `--yeni` ile ilk kurulum), `.github/workflows/ci.yml` (her PR'da test + Linux derleme).
- **`setup.sh`:** .NET 10 SDK (apt, olmazsa resmi `dotnet-install.sh`); mimariye göre self-contained derleme.
- **Silinen:** `.github/workflows/mhrs-bot.yml` (botu GitHub'da VPN'le dakikada bir çalıştıran, programın okumadığı değişken adlarını kullanan eski iş akışı), `MHRS-OtomatikRandevu/.github/workflows/deploy.yml` (alt klasörde olduğu için hiç çalışmıyordu), `MHRS-OtomatikRandevu/install-clean.sh` (.NET 7 kuran eski kurulum).

### Faz 6 — Yeni özellikler · PR #6
- **Birden fazla doktor:** `MHRS_DOCTOR_ID=34448,2783` → her denemede hepsi sorgulanır, en erken slot alınır.
- **Telegram komutları** (`TELEGRAM_COMMANDS=1`, varsayılan kapalı): `/durum`, `/duraklat`, `/devam`, `/tarih GG-AA-YYYY [GG-AA-YYYY]`, `/hedef`. Yalnızca `TELEGRAM_CHAT_ID` sohbetinden; açılışta eski mesajlar atlanır; aynı token'ı başka süreç dinliyorsa (409) kapanır.
- **Sabah özeti** 09:00 (`TELEGRAM_DAILY_REPORT=0` ile kapanır).
- **Eklenen:** `Services/TelegramCommandListener.cs`, `Program.Commands.cs`

### Faz 7 — e-Nabız / e-Devlet token modu · PR #7
- **Arka plan:** MHRS sitesi Şubat 2026'da TC + şifre girişini kaldırdı (sitede artık `vatandas/login/v2` + captcha). Bot eski `vatandas/login` adresini kullanıyor; Kasım 2026'da hâlâ çalışıyor ama her an kapanabilir.
- **Bulgu:** Site, hangi yöntemle girilirse girilsin JWT'yi `localStorage["token-v-mhrs"]` içinde CryptoJS AES ile (istemci kodunda sabit parola) saklıyor.
- `MHRS_AUTH_MODE=token`: bot **hiç şifre denemez**. Kullanıcı tarayıcıda e-Nabız/e-Devlet ile girer, `copy(localStorage.getItem('token-v-mhrs'))` ile değeri alır, Telegram'dan `/token <değer>` gönderir.
- Oturum yoksa/biterse/düşerse arama duraklar, Telegram'dan yeni oturum istenir (2 saatte bir hatırlatma); bitmeden ~1 saat önce uyarı.
- Bot e-Nabız/e-Devlet'e kendisi girmez (captcha/SMS atlatılmaz).
- **Eklenen:** `Utils/MhrsTokenUtil.cs`

---

## Ayar Değişiklikleri (`.env`)

| Ayar | Durum | Açıklama |
|---|---|---|
| `MHRS_END_DATE` | **Davranış değişti** | Bitiş günü dahil; geçince bot durur; geçersizse bot başlamaz |
| `MHRS_START_DATE` | **Davranış değişti** | Geçersizse bot başlamaz (önceden sessizce "bugün") |
| `MHRS_DOCTOR_ID` | **Genişledi** | Virgülle birden fazla |
| `MHRS_SCHEDULE_TIMES` | **Doğrulanıyor** | `SS:DD` (iki haneli) olmalı |
| `TELEGRAM_NOTIFY_FREQUENCY` | **Doğrulanıyor** | 0/negatif → uyarı, 10 kullanılır |
| `MHRS_CONTINUOUS` | Yeni | `1` = sürekli arama |
| `MHRS_INTERVAL_SECONDS` | Yeni | Denemeler arası bekleme (varsayılan 120) |
| `TELEGRAM_COMMANDS` | Yeni | `1` = komutları dinle |
| `TELEGRAM_DAILY_REPORT` | Yeni | `0` = sabah özetini kapat |
| `MHRS_AUTH_MODE` | Yeni | `password` (varsayılan) / `token` |

## Bilinen Davranış Farkları
- İlk 5 deneme arası bekleme 4 dk → 3 dk (mesajlarda zaten 3 dk yazıyordu).
- Sihirbazla kurulan botlar bitiş tarihi geçince artık duruyor.
- Randevu alırken oturum düşer ve yeniden giriş de başarısız olursa: geçici hata (çıkış 1, systemd yeniden başlatır).

## Kod Yapısı (yeni)

```
MHRS-OtomatikRandevu/
  Program.cs            Main, .env yükleme, ayarları uygulama
  BotConfig.cs          .env okuma + doğrulama
  Program.Target.cs     Hedef (il/klinik/hastane/doktor) doğrulama
  Program.Search.cs     Arama döngüsü
  Program.Slots.cs      Slot arama/seçme, randevu alma
  Program.Auth.cs       Giriş, oturum kurtarma, token modu
  Program.Commands.cs   Telegram komutları, canlı durum
  Program.Schedule.cs   Saat pencereleri
  Program.Output.cs     Log (döndürmeli), çıkış
  SetupWizard.cs        --setup sihirbazı
  Services/  ClientService, NotificationService, TelegramCommandListener
  Utils/     ApiErrorUtil, JwtTokenUtil, MhrsTokenUtil, ...
MHRS-OtomatikRandevu.Tests/   81 xUnit testi
deploy.sh  setup.sh  install_service.sh  .github/workflows/ci.yml
```

---

## Kalan İşler

- [ ] **Sunucudaki botları yeni sürüme geçir.** Sunucuda hâlâ Faz 0 yamalı eski sürüm çalışıyor. Çalışan tek günlük arama bitince `./deploy.sh <hedef>` ile geçir, canlı MHRS'de uçtan uca dene.
- [ ] **Token modunu gerçek e-Nabız oturumuyla dene.** Şifre çözme `openssl` ile üretilmiş örnekle doğrulandı; gerçek oturumla henüz denenmedi.
- [ ] **Telegram komutlarını gerçek botla dene** (`TELEGRAM_COMMANDS=1`). Aynı bot token'ını yalnızca bir bot dinleyebilir.
- [ ] **Eski GitHub secret'larını sil** (`MHRS_TC`, `MHRS_PASSWORD`, `IL_ID`, `ILCE_ID`, `KLINIK_ID`, `VPN_*`) — artık kullanan iş akışı yok. Onay bekliyor.
- [ ] (İsteğe bağlı) Model sınıflarındaki nullable uyarılarını temizle.
- [ ] (İsteğe bağlı) `SetupWizard.cs`'teki `.user_history` düz metin şifre saklamayı kaldır veya şifrele.

## Geliştirme

```bash
dotnet test MHRS-OtomatikRandevu.Tests      # testler
./deploy.sh <hedef>                         # sunucuya yükle (README: deploy.sh)
```
