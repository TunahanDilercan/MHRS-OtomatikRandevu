#!/bin/bash

# MHRS Bot - Tam Kurulum Scripti
# Bu script gerekli bağımlılıkları yükler, projeyi derler ve kurulum sihirbazını başlatır.

# Renkler
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}
 __  __ _    _ _____   _____   _____           _        _ _ 
|  \/  | |  | |  __ \ / ____| |_   _|         | |      | | |
| \  / | |__| | |__) | (___     | |  _ __  ___| |_ __ _| | |
| |\/| |  __  |  _  / \___ \    | | | '_ \/ __| __/ _\` | | |
| |  | | |  | | | \ \ ____) |  _| |_| | | \__ \ || (_| | | |
|_|  |_|_|  |_|_|  \_\_____/  |_____|_| |_|___/\__\__,_|_|_|
                                                            
=== KURULUM VE YAPILANDIRMA BAŞLATILIYOR ===
${NC}"

# 1. BAĞIMLILIK KONTROLÜ
echo -e "${BLUE}[1/4] Gerekli bağımlılıklar kontrol ediliyor...${NC}"

# .NET SDK Kontrol
if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}.NET SDK bulunamadı! Yükleniyor...${NC}"
    
    # OS Tespiti
    if [ -f /etc/os-release ]; then
        . /etc/os-release
        OS=$NAME
        VER=$VERSION_ID
        
        if [[ "$OS" == *"Ubuntu"* ]] || [[ "$OS" == *"Debian"* ]]; then
            echo "Ubuntu/Debian sistemi tespit edildi. Kurulum başlatılıyor..."
            
            # Gerekli araçlar
            if ! command -v wget &> /dev/null; then sudo apt-get install -y wget; fi
            
            # Microsoft paket imzalama anahtarını ekle
            wget https://packages.microsoft.com/config/ubuntu/$VER/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
            sudo dpkg -i packages-microsoft-prod.deb
            rm packages-microsoft-prod.deb
            
            # SDK Kur
            sudo apt-get update
            sudo apt-get install -y dotnet-sdk-7.0
            
        else
            echo -e "${RED}Otomatik kurulum sadece Ubuntu/Debian için destekleniyor.${NC}"
            echo "Lütfen .NET 7.0 SDK'yı manuel kurunuz: https://dotnet.microsoft.com/download"
            exit 1
        fi
    else
        echo -e "${RED}İşletim sistemi tespit edilemedi.${NC}"
        echo "Lütfen .NET 7.0 SDK'yı manuel kurunuz."
        exit 1
    fi
else
    echo -e "${GREEN}.NET SDK zaten yüklü. Devam ediliyor...${NC}"
fi

# 2. PROJE DERLEME
echo -e "\n${BLUE}[2/4] Proje hazırlanıyor...${NC}"
cd MHRS-OtomatikRandevu || { echo "Proje klasörü bulunamadı!"; exit 1; }

echo "Paketler geri yükleniyor (restore)..."
dotnet restore

echo "Derleniyor (build)..."
dotnet build --configuration Release

# 3. SİHİRBAZI BAŞLAT
echo -e "\n${BLUE}[3/4] Konfigürasyon sihirbazı başlatılıyor...${NC}"
# --setup parametresi ile C# tarafındaki sihirbazı çalıştır
dotnet run --configuration Release -- --setup

# Geri dön
cd ..

# Scriptleri çalıştırılabilir yap
chmod +x *.sh

# 4. BAŞLATMA
echo -e "\n${BLUE}[4/4] Kurulum tamamlandı!${NC}"
read -p "Botu şimdi başlatmak ister misiniz? (E/h): " start_now
start_now=${start_now:-E}

if [[ $start_now =~ ^[Ee]$ ]]; then
    ./bot.sh
else
    echo -e "${GREEN}Daha sonra başlatmak için './bot.sh' komutunu kullanabilirsiniz.${NC}"
    echo -e "${GREEN}Ayarları değiştirmek için './wizard.sh' komutunu kullanabilirsiniz.${NC}"
fi
