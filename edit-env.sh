#!/bin/bash

# .env Düzenleme Aracı
# Kullanıcının konfigürasyon dosyasını güvenli bir şekilde düzenlemesini sağlar.

GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m'

ENV_FILE="MHRS-OtomatikRandevu/.env"

if [ ! -f "$ENV_FILE" ]; then
    echo -e "${RED}[HATA] .env dosyası bulunamadı! Önce kurulumu yapın.${NC}"
    exit 1
fi

echo -e "${BLUE}
=======================================
   .ENV YAPILANDIRMA EDİTÖRÜ
=======================================
${NC}"
echo "Dosya: $ENV_FILE"
echo "Çıkış için: Ctrl+X, sonra Y, sonra Enter"
echo "---------------------------------------"
read -p "Düzenlemek için Enter'a basın..." 

# Nana ile aç
nano "$ENV_FILE"

echo -e "\n${GREEN}✔ Düzenleme tamamlandı.${NC}"
echo "Değişikliklerin geçerli olması için botu yeniden başlatın."
