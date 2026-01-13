#!/bin/bash

# MHRS Bot - Hızlı Yapılandırma Sihirbazı
# Sadece .env dosyasını yeniden oluşturmak için kullanılır.

GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}=== MHRS Yapılandırma Sihirbazı ===${NC}"

cd MHRS-OtomatikRandevu || exit

# Sihirbazı çalıştır
if [ -f "./bin/publish/MHRS-OtomatikRandevu" ]; then
    ./bin/publish/MHRS-OtomatikRandevu --setup
else
    dotnet run --configuration Release -- --setup
fi

echo -e "${GREEN}Ayarlar güncellendi!${NC}"
echo -e "Botu başlatmak için './bot.sh' kullanabilirsiniz."
