#!/bin/bash

# MHRS Bot - Systemd Servis Kurulumu
# Bu script botu arka planda çalışan bir servis haline getirir.

GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m'

if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}Lütfen bu scripti root yetkisiyle çalıştırın (sudo ./install_service.sh)${NC}"
  exit 1
fi

SERVICE_NAME="mhrs-bot"
SERVICE_FILE="/etc/systemd/system/$SERVICE_NAME.service"

# Tam yolu al
CURRENT_DIR=$(pwd)
PROJECT_DIR="$CURRENT_DIR/MHRS-OtomatikRandevu"
DOTNET_PATH=$(which dotnet)

if [ -z "$DOTNET_PATH" ]; then
    echo -e "${RED}dotnet bulunamadı! Lütfen önce ./setup.sh çalıştırın.${NC}"
    exit 1
fi

echo -e "${BLUE}Servis dosyası oluşturuluyor...${NC}"

cat > $SERVICE_FILE <<EOF
[Unit]
Description=MHRS Otomatik Randevu Botu
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=$PROJECT_DIR
ExecStart=$DOTNET_PATH run --configuration Release
Restart=always
RestartSec=30
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

[Install]
WantedBy=multi-user.target
EOF

echo -e "${BLUE}Servis etkinleştiriliyor...${NC}"
systemctl daemon-reload
systemctl enable $SERVICE_NAME
systemctl restart $SERVICE_NAME

echo -e "${GREEN}✅ Servis kuruldu ve başlatıldı!${NC}"
echo -e "Durum kontrolü için: ${BLUE}systemctl status $SERVICE_NAME${NC}"
echo -e "Logları izlemek için: ${BLUE}journalctl -u $SERVICE_NAME -f${NC}"
