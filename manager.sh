#!/bin/bash

# MHRS Bot Yönetim Paneli
# Tüm işlemleri tek bir yerden yönetmek için menü sistemi.

# Renkler
GREEN='\033[0;32m'
RED='\033[0;31m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

SERVICE_NAME="mhrs-bot"
LOG_FILE="MHRS-OtomatikRandevu/randevu_log.txt"

# Yetki kontrolü
check_root() {
    if [ "$EUID" -ne 0 ]; then
        echo -e "${RED}Bu işlem için root yetkisi gerekiyor. Lütfen 'sudo ./manager.sh' ile çalıştırın.${NC}"
        return 1
    fi
    return 0
}

# Durum kontrolü
get_status_detail() {
    if systemctl is-active --quiet $SERVICE_NAME; then
        STATUS="${GREEN}ÇALIŞIYOR (RUNNING)${NC}"
        
        # Deneme sayısını bul
        if [ -f "$LOG_FILE" ]; then
            LAST_ATTEMPT=$(grep -o "Deneme #[0-9]*" "$LOG_FILE" | tail -1 | awk '{print $2}')
            LAST_TIME=$(tail -n 1 "$LOG_FILE" | awk -F'[\\[\\]]' '{print $2}')
            
            if [ ! -z "$LAST_ATTEMPT" ]; then
                echo -e "$STATUS | ${YELLOW}Son İşlem: ${LAST_ATTEMPT}${NC} (${LAST_TIME})"
            else
                echo -e "$STATUS | ${YELLOW}Henüz deneme yapılmadı veya log yok${NC}"
            fi
        else
            echo -e "$STATUS"
        fi
    else
        echo -e "${RED}DURDURULDU (STOPPED)${NC}"
    fi
}

start_bot() {
    check_root || return
    echo "Bot başlatılıyor..."
    systemctl start $SERVICE_NAME
    sleep 2
    systemctl status $SERVICE_NAME --no-pager
}

# Durdur
stop_bot() {
    check_root || return
    echo "Bot durduruluyor..."
    systemctl stop $SERVICE_NAME
    echo -e "${RED}Bot durduruldu.${NC}"
}

# Yeniden Başlat
restart_bot() {
    check_root || return
    echo "Bot yeniden başlatılıyor..."
    systemctl restart $SERVICE_NAME
    echo -e "${GREEN}Bot yeniden başlatıldı.${NC}"
}

# Logları izle
view_logs() {
    echo -e "${BLUE}Loglar açılıyor... (Çıkmak için Ctrl+C)${NC}"
    # Önce dosya var mı bak
    if [ -f "$LOG_FILE" ]; then
        tail -f "$LOG_FILE"
    else
        # Yoksa servis loglarına bak
        journalctl -u $SERVICE_NAME -f
    fi
}

# Temizlik
clean_cache() {
    check_root || return
    echo -e "${YELLOW}Token ve geçici dosyalar temizleniyor...${NC}"
    stop_bot
    rm -f MHRS-OtomatikRandevu/token.txt
    rm -f MHRS-OtomatikRandevu/randevu_basarili.txt
    rm -f MHRS-OtomatikRandevu/randevu_log.txt
    echo -e "${GREEN}Temizlik tamamlandı.${NC}"
    read -p "Bot tekrar başlatılsın mı? (E/h): " choice
    if [[ "$choice" =~ ^[Ee]$ ]] || [[ -z "$choice" ]]; then
        start_bot
    fi
}

# Wizard
run_wizard() {
    echo -e "${BLUE}Ayarlar menüsü açılıyor...${NC}"
    
    WAS_RUNNING=0
    # Servis çalışıyorsa geçici olarak durdur
    if systemctl is-active --quiet $SERVICE_NAME; then
        echo -e "${YELLOW}Ayarların etkinleşmesi için bot geçici olarak durduruluyor...${NC}"
        systemctl stop $SERVICE_NAME
        WAS_RUNNING=1
    fi
    
    ./wizard.sh
    
    if [ $WAS_RUNNING -eq 1 ]; then
        echo -e "${GREEN}Ayarlar güncellendi. Bot otomatik olarak yeniden başlatılıyor...${NC}"
        start_bot
    else
        read -p "Bot başlatılsın mı? (E/h): " start_choice
        if [[ "$start_choice" =~ ^[Ee]$ ]] || [[ -z "$start_choice" ]]; then
            start_bot
        fi
    fi
}

# Servis kurulumu
install_service() {
    check_root || return
    ./install_service.sh
}

# Başarılı randevular
view_success() {
    echo -e "${BLUE}=== BAŞARILI RANDEVULAR ===${NC}"
    if [ -f "MHRS-OtomatikRandevu/randevu_basarili.txt" ]; then
        echo -e "${GREEN}"
        cat MHRS-OtomatikRandevu/randevu_basarili.txt
        echo -e "${NC}"
        echo -e "---------------------------------------"
        read -p "Bu randevu kaydını silip botu sıfırlamak (yeni randevu için) ister misiniz? (E/h): " choice
        if [[ "$choice" =~ ^[Ee]$ ]] || [[ -z "$choice" ]]; then
            echo -e "${YELLOW}Başarılı randevu kaydı siliniyor...${NC}"
            stop_bot
            rm -f MHRS-OtomatikRandevu/randevu_basarili.txt
            echo -e "${GREEN}Kayıt silindi.${NC}"
            
            echo -e "1) Aynı ayarlarla botu tekrar başlat"
            echo -e "2) Yeni randevu ayarları yap (Sihirbaz)"
            echo -e "3) Sadece kaydı sil ve çık"
            read -p "Seçiminiz: " next_action
            
            case $next_action in
                1) start_bot ;;
                2) run_wizard ;;
                *) echo "İşlem tamamlandı." ;;
            esac
        fi
    else
        echo -e "${YELLOW}Henüz başarılı bir randevu kaydı bulunmuyor.${NC}"
        read -p "Devam etmek için Enter..."
    fi
}

while true; do
    clear
    echo -e "${BLUE}=======================================${NC}"
    echo -e "   MHRS OTOMATİK RANDEVU - YÖNETİM   "
    echo -e "${BLUE}=======================================${NC}"
    echo -e "Bot Durumu: $(get_status_detail)"
    echo -e "${BLUE}---------------------------------------${NC}"
    echo -e "1) 🟢 Başlat (Start)"
    echo -e "2) 🔴 Durdur (Stop)"
    echo -e "3) 🔄 Yeniden Başlat (Restart)"
    echo -e "4) 📋 Logları İzle"
    echo -e "5) 🏆 Başarılı Randevuları Gör"
    echo -e "6) ⚙️  Ayarları Düzenle (Sihirbaz - Sıfırdan)"
    echo -e "7) 📝 Ayarları Elle Düzenle (.env Editör)"
    echo -e "8) 🚑 Hata Onar / Sıfırla (Reset & Clear)"
    echo -e "9) 🛠️  Servisi Kur/Onar"
    echo -e "0) ❌ Çıkış"
    echo -e "${BLUE}---------------------------------------${NC}"
    read -p "Seçiminiz: " option

    case $option in
        1) start_bot; read -p "Devam etmek için Enter..." ;;
        2) stop_bot; read -p "Devam etmek için Enter..." ;;
        3) restart_bot; read -p "Devam etmek için Enter..." ;;
        4) view_logs ;;
        5) view_success ;;
        6) run_wizard; read -p "Devam etmek için Enter..." ;;
        7) ./edit-env.sh; read -p "Devam etmek için Enter..." ;;
        8) clean_cache; read -p "Devam etmek için Enter..." ;;
        9) install_service; read -p "Devam etmek için Enter..." ;;
        0) echo "Güle güle!"; exit 0 ;;
        *) echo "Geçersiz seçim!"; sleep 1 ;;
    esac
done
