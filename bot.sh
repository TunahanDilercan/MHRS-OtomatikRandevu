#!/bin/bash

# MHRS Bot Başlatma Scripti
# Botu başlatır ve çalıştırır.

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

if [ ! -f "MHRS-OtomatikRandevu/.env" ]; then
    echo -e "${RED}Konfigürasyon dosyası (.env) bulunamadı!${NC}"
    echo "Lütfen önce kurulumu veya sihirbazı çalıştırın:"
    echo "./setup.sh (Tam Kurulum)"
    echo "./wizard.sh (Sadece Ayarlar)"
    exit 1
fi

cd MHRS-OtomatikRandevu || exit

echo -e "${GREEN}Bot başlatılıyor... (Detaylı loglar için yeni terminalde ./logs.sh)${NC}"
echo -e "${GREEN}Durdurmak için Ctrl+C${NC}"

# Release modunda çalıştır
dotnet run --configuration Release
