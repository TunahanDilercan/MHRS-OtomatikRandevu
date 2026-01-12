#!/bin/bash

# MHRS Bot GitHub'dan Kurulum Scripti
# Kullanım: curl -sSL https://raw.githubusercontent.com/TunahanDilercan/MHRS-OtomatikRandevu/main/MHRS-OtomatikRandevu/install.sh | bash

echo "=== MHRS Bot GitHub'dan Kurulum ==="

# Renk kodları
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# GitHub repository bilgileri
GITHUB_USER="TunahanDilercan"
GITHUB_REPO="MHRS-OtomatikRandevu"
GITHUB_BRANCH="main"

# Hata kontrolü
set -e

echo -e "${YELLOW}1. Sistem güncelleniyor...${NC}"
sudo apt-get update

echo -e "${YELLOW}2. Gerekli paketler kuruluyor...${NC}"
sudo apt-get install -y git curl wget unzip

echo -e "${YELLOW}3. .NET SDK 7.0 kuruluyor...${NC}"
# Microsoft repository key ekle
if [ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]; then
    wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
    sudo dpkg -i packages-microsoft-prod.deb
    rm packages-microsoft-prod.deb
    sudo apt-get update
fi

# .NET SDK kur
sudo apt-get install -y dotnet-sdk-7.0

echo -e "${GREEN}.NET SDK kuruldu: $(dotnet --version)${NC}"

echo -e "${YELLOW}4. Proje GitHub'dan indiriliyor...${NC}"
PROJECT_DIR="$HOME/mhrs-bot"

# Eğer dizin varsa temizle
if [ -d "$PROJECT_DIR" ]; then
    echo -e "${YELLOW}Mevcut kurulum temizleniyor...${NC}"
    rm -rf "$PROJECT_DIR"
fi

# Projeyi clone et
git clone "https://github.com/$GITHUB_USER/$GITHUB_REPO.git" "$PROJECT_DIR"
cd "$PROJECT_DIR/MHRS-OtomatikRandevu"

echo -e "${YELLOW}5. Proje derleniyor...${NC}"
dotnet restore
dotnet build --configuration Release

echo -e "${YELLOW}6. .env dosyası hazırlanıyor...${NC}"
if [ ! -f .env ]; then
    cat > .env << 'EOF'
# MHRS Bot Ayarları
MHRS_TC=
MHRS_PASSWORD=

# Lokasyon ID'leri
MHRS_PROVINCE_ID=
MHRS_DISTRICT_ID=
MHRS_CLINIC_ID=
MHRS_HOSPITAL_ID=-1
MHRS_PLACE_ID=-1
MHRS_DOCTOR_ID=-1

# Tarih Ayarları
MHRS_START_DATE=2025-07-07
MHRS_END_DATE=

# Telegram Bot Bildirimleri
TELEGRAM_BOT_TOKEN=
TELEGRAM_CHAT_ID=
TELEGRAM_NOTIFY_FREQUENCY=10

# E-posta Bildirimleri (opsiyonel)
EMAIL_SMTP_HOST=
EMAIL_USERNAME=
EMAIL_PASSWORD=
EMAIL_TO=

# SMS Bildirimleri (opsiyonel)
TWILIO_ACCOUNT_SID=
TWILIO_AUTH_TOKEN=
TWILIO_FROM_PHONE=
TWILIO_TO_PHONE=
EOF
    chmod 600 .env
    echo -e "${GREEN}.env dosyası oluşturuldu${NC}"
else
    echo -e "${YELLOW}.env dosyası zaten mevcut${NC}"
fi

echo -e "${YELLOW}7. Systemd servis dosyası oluşturuluyor...${NC}"
sudo tee /etc/systemd/system/mhrs-bot.service > /dev/null << EOF
[Unit]
Description=MHRS Otomatik Randevu Botu
After=network.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$PROJECT_DIR/MHRS-OtomatikRandevu
ExecStart=/usr/bin/dotnet run
Restart=always
RestartSec=30
Environment=ASPNETCORE_ENVIRONMENT=Production
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

echo -e "${YELLOW}8. Dosya izinleri ayarlanıyor...${NC}"
chmod +x *.sh 2>/dev/null || true
chmod 600 .env

echo -e "${YELLOW}9. Systemd servisi aktifleştiriliyor...${NC}"
sudo systemctl daemon-reload
sudo systemctl enable mhrs-bot

echo -e "${GREEN}=== KURULUM TAMAMLANDI! ===${NC}"
echo ""
echo -e "${BLUE}📁 Proje konumu: ${PROJECT_DIR}/MHRS-OtomatikRandevu${NC}"
echo -e "${BLUE}📝 .env dosyasını düzenleyin: ${NC}"
echo -e "   ${YELLOW}nano ${PROJECT_DIR}/MHRS-OtomatikRandevu/.env${NC}"
echo ""
echo -e "${BLUE}🚀 Bot yönetim komutları:${NC}"
echo -e "   ${GREEN}cd ${PROJECT_DIR}/MHRS-OtomatikRandevu${NC}"
echo -e "   ${GREEN}./bot-manager.sh start${NC}     # Botu başlat"
echo -e "   ${GREEN}./bot-manager.sh status${NC}    # Durum kontrol"
echo -e "   ${GREEN}./bot-manager.sh logs${NC}      # Log takibi"
echo -e "   ${GREEN}./bot-manager.sh stop${NC}      # Botu durdur"
echo ""
echo -e "${YELLOW}⚠️  .env dosyasını düzenledikten sonra botu başlatın!${NC}"
echo ""
