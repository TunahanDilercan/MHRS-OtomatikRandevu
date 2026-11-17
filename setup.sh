#!/bin/bash

# MHRS Bot - Hızlı Kurulum ve Yapılandırma
# Sürüm: 2.1 (Clean & Simple)

GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m'

clear
echo -e "${BLUE}
 __  __ _    _ _____   _____   _____           _        _ _ 
|  \/  | |  | |  __ \ / ____| |_   _|         | |      | | |
| \  / | |__| | |__) | (___     | |  _ __  ___| |_ __ _| | |
| |\/| |  __  |  _  / \___ \    | | | '_ \/ __| __/ _\` | | |
| |  | | |  | | | \ \ ____) |  _| |_| | | \__ \ || (_| | | |
|_|  |_|_|  |_|_|  \_\_____/  |_____|_| |_|___/\__\__,_|_|_|
                                                            
=== KURULUM SİHİRBAZI ===
${NC}"

# Hata yakalama fonksiyonu
run_step() {
    msg="$1"
    cmd="$2"
    echo -ne "${BLUE}[INFO]${NC} $msg... "
    
    # Komutu çalıştır ve çıktıyı sakla
    if eval "$cmd" > /tmp/mhrs_install.log 2>&1; then
        echo -e "${GREEN}BAŞARILI${NC}"
    else
        echo -e "${RED}HATA${NC}"
        echo -e "\n${RED}!!! Kurulum sırasında bir hata oluştu !!!${NC}"
        echo "----------------------------------------"
        cat /tmp/mhrs_install.log
        echo "----------------------------------------"
        echo "Çözüm için yukarıdaki hatayı kontrol edin."
        exit 1
    fi
}

# 1. SDK Kontrol (.NET 10 gerekli)
has_sdk10() { command -v dotnet &> /dev/null && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; }

if has_sdk10; then
    echo -e "${GREEN}[OK]${NC} .NET 10 SDK zaten yüklü."
else
    echo -e "${RED}[UYARI]${NC} .NET 10 SDK bulunamadı! Otomatik yükleniyor..."
    # Önce dağıtımın paket deposu, olmazsa Microsoft'un resmi kurulum betiği (her Linux'ta çalışır)
    if command -v apt-get &> /dev/null && apt-get update > /tmp/mhrs_install.log 2>&1 \
        && apt-get install -y dotnet-sdk-10.0 >> /tmp/mhrs_install.log 2>&1 && has_sdk10; then
        echo -e "${GREEN}[OK]${NC} .NET 10 SDK apt ile kuruldu."
    else
        run_step ".NET 10 SDK resmi betikle kuruluyor" \
            "curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet"
        has_sdk10 || { echo -e "${RED}[HATA]${NC} .NET 10 SDK kurulamadı."; exit 1; }
    fi
fi

# 2. Proje Derleme
if [ ! -d "MHRS-OtomatikRandevu" ]; then
    echo -e "${RED}[HATA] Proje klasörü (MHRS-OtomatikRandevu) bulunamadı!${NC}"
    exit 1
fi

cd MHRS-OtomatikRandevu || exit

echo -e "\n--- Proje Hazırlanıyor ---"
# Restore
run_step "Bağımlılıklar yükleniyor" "dotnet restore"

# Publish (Derleme ve Çıktı Alma)
# Sunucu mimarisine göre self-contained tek dosya: çalıştırmak için ayrıca .NET runtime gerekmez
case "$(uname -m)" in
    aarch64|arm64) RID=linux-arm64 ;;
    *) RID=linux-x64 ;;
esac
run_step "Uygulama derleniyor ve hazırlanıyor ($RID)" "dotnet publish --configuration Release -r $RID --self-contained true -p:PublishSingleFile=true --nologo -v q -o bin/publish"

# Scriptleri yetkilendir
chmod +x *.sh 2>/dev/null

# 3. Sihirbaz
echo -e "\n--- Yapılandırma ---"
echo -e "${BLUE}Kurulum sihirbazı başlatılıyor...${NC}"
sleep 1

# Sihirbazı çalıştır (Derlenmiş dosya üzerinden)
./bin/publish/MHRS-OtomatikRandevu --setup

# Geri dön
cd ..
chmod +x *.sh 2>/dev/null

# 4. Bitiş Menüsü
echo -e "\n${GREEN}✔ Kurulum Tamamlandı!${NC}"
echo "Ne yapmak istersiniz?"
echo "1) Botu Başlat (Start)"
echo "2) Çıkış (Exit)"

while true; do
    read -p "Seçiminiz [1-2]: " choice
    case $choice in
        1) 
            ./bot.sh
            break
            ;;
        2) 
            echo "Çıkış yapılıyor. Başlatmak için './bot.sh' kullanabilirsiniz."
            break 
            ;;
        *) echo "Lütfen 1 veya 2 giriniz." ;;
    esac
done
