#!/bin/bash

# MHRS Bot - Log İzleyici
# Botun log dosyasını canlı olarak takip eder.

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

LOG_FILE="MHRS-OtomatikRandevu/randevu_log.txt"

echo -e "${GREEN}Loglar izleniyor... (Çıkmak için Ctrl+C)${NC}"

if [ ! -f "$LOG_FILE" ]; then
    echo -e "${RED}Log dosyası henüz oluşmamış ($LOG_FILE). Bot hiç çalışmamış olabilir.${NC}"
    echo "Botu başlattıktan sonra tekrar deneyin."
    exit 1
fi

tail -f "$LOG_FILE"
