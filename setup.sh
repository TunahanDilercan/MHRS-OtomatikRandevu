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

# 1. SDK Kontrol
if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}[UYARI]${NC} .NET SDK bulunamadı! Otomatik yükleniyor..."
    
    if [ -f /etc/os-release ]; then
        . /etc/os-release
        if [[ "$ID" == "ubuntu" || "$ID" == "debian" ]]; then
             run_step "Microsoft anahtarları ekleniyor" "wget https://packages.microsoft.com/config/ubuntu/$VERSION_ID/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb"
             run_step "Paket listesi güncelleniyor" "apt-get update"
             run_step ".NET SDK yükleniyor" "apt-get install -y dotnet-sdk-7.0"
        else
            echo "Bu sistem için otomatik kurulum desteklenmiyor. Lütfen .NET 7 SDK manuel kurun."
            exit 1
        fi
    else
        echo "İşletim sistemi algılanamadı."
        exit 1
    fi
else
    echo -e "${GREEN}[OK]${NC} .NET SDK zaten yüklü."
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

# Build (Sessiz mod)
run_step "Kod derleniyor" "dotnet build --configuration Release --nologo -v q"

# Scriptleri yetkilendir
chmod +x *.sh 2>/dev/null

# 3. Sihirbaz
echo -e "\n--- Yapılandırma ---"
echo -e "${BLUE}Kurulum sihirbazı başlatılıyor...${NC}"
sleep 1

# Sihirbazı çalıştır
dotnet run --configuration Release -- --setup

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
