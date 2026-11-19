#!/usr/bin/env bash
# MHRS botunu yerelde derleyip sunucuya yükler, servisi yeniden başlatır ve sağlık kontrolü yapar.
# Sunucuya .NET kurulmaz: tek dosyalık, self-contained bir Linux uygulaması gönderilir.
#
# Kullanım:
#   ./deploy.sh <hedef> [--yeni]
#     <hedef>  : sunucudaki /opt/mhrs-<hedef> klasörü ve mhrs-<hedef> servisi (ör. konya, karaman)
#     --yeni   : klasör/servis yoksa oluştur (.env'i sonra elle koyun; servis başlatılmaz)
#
# Ortam değişkenleri (varsayılanlar parantez içinde):
#   MHRS_SSH_HOST (zorunlu)   MHRS_SSH_USER (root)   MHRS_SSH_PORT (22)
#   MHRS_SSH_KEY  (boş = ssh varsayılanı)            MHRS_RID (linux-x64)
#
# Örnek:
#   MHRS_SSH_HOST=1.2.3.4 MHRS_SSH_PORT=2222 MHRS_SSH_KEY=~/.ssh/sunucu ./deploy.sh konya
set -euo pipefail

TARGET="${1:-}"
NEW="${2:-}"
if [[ -z "$TARGET" || ! "$TARGET" =~ ^[a-z0-9-]+$ ]]; then
    echo "Kullanım: $0 <hedef> [--yeni]   (hedef: küçük harf, rakam, tire)" >&2
    exit 1
fi
: "${MHRS_SSH_HOST:?MHRS_SSH_HOST ayarlanmalı}"
USER_="${MHRS_SSH_USER:-root}"
PORT="${MHRS_SSH_PORT:-22}"
RID="${MHRS_RID:-linux-x64}"
SSH_OPTS=(-p "$PORT" -o BatchMode=yes -o ConnectTimeout=15)
SCP_OPTS=(-P "$PORT" -o BatchMode=yes -o ConnectTimeout=15 -q)
if [[ -n "${MHRS_SSH_KEY:-}" ]]; then
    SSH_OPTS+=(-i "$MHRS_SSH_KEY"); SCP_OPTS+=(-i "$MHRS_SSH_KEY")
fi
REMOTE="$USER_@$MHRS_SSH_HOST"
DIR="/opt/mhrs-$TARGET"
SVC="mhrs-$TARGET"
# Göreli yollar: Windows Git Bash'te /d/... gibi mutlak yollar dotnet'e seçenek (/switch) gibi görünüyor
cd "$(dirname "$0")"
OUT=".deploy-out"
rm -rf "$OUT"
trap 'rm -rf "$OUT"' EXIT

echo "==> Testler"
dotnet test MHRS-OtomatikRandevu.Tests --nologo -v q

echo "==> Derleniyor ($RID, self-contained tek dosya)"
dotnet publish MHRS-OtomatikRandevu/MHRS-OtomatikRandevu.csproj -c Release -r "$RID" \
    --self-contained true -p:PublishSingleFile=true --nologo -v q -o "$OUT"

if [[ "$NEW" == "--yeni" ]]; then
    echo "==> $DIR ve $SVC oluşturuluyor"
    ssh "${SSH_OPTS[@]}" "$REMOTE" "mkdir -p '$DIR' && chmod 700 '$DIR'"
    scp "${SCP_OPTS[@]}" "$OUT/MHRS-OtomatikRandevu" "$REMOTE:$DIR/MHRS-OtomatikRandevu"
    ssh "${SSH_OPTS[@]}" "$REMOTE" "chmod 755 '$DIR/MHRS-OtomatikRandevu' && cat > /etc/systemd/system/$SVC.service" <<EOF
[Unit]
Description=MHRS Otomatik Randevu ($TARGET)
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=3600
StartLimitBurst=10

[Service]
WorkingDirectory=$DIR
ExecStart=$DIR/MHRS-OtomatikRandevu
Environment=TZ=Europe/Istanbul
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
StandardInput=null
# 0 = randevu alındı, 1 = geçici hata (yeniden başlat), 2 = kalıcı hata/iş bitti (başlatma)
Restart=on-failure
RestartSec=60
RestartPreventExitStatus=2
Nice=10
CPUQuota=25%
MemoryMax=160M

[Install]
WantedBy=multi-user.target
EOF
    ssh "${SSH_OPTS[@]}" "$REMOTE" "systemctl daemon-reload"
    echo "Hazır. Şimdi $DIR/.env dosyasını oluşturun (chmod 600) ve başlatın:"
    echo "  systemctl enable --now $SVC"
    exit 0
fi

echo "==> Sunucu durumu kontrol ediliyor"
ssh "${SSH_OPTS[@]}" "$REMOTE" "test -d '$DIR' && test -f '$DIR/.env'" \
    || { echo "HATA: $DIR veya .env yok. İlk kurulum için: $0 $TARGET --yeni" >&2; exit 1; }
if ssh "${SSH_OPTS[@]}" "$REMOTE" "tail -1 '$DIR/randevu_log.txt' 2>/dev/null | grep -q 'RANDEVU BULUNDU'"; then
    echo "HATA: bot şu an randevu alıyor olabilir; birkaç saniye sonra tekrar deneyin." >&2
    exit 1
fi

echo "==> Yükleniyor"
scp "${SCP_OPTS[@]}" "$OUT/MHRS-OtomatikRandevu" "$REMOTE:$DIR/MHRS-OtomatikRandevu.new"
ssh "${SSH_OPTS[@]}" "$REMOTE" "cd '$DIR' && chmod 755 MHRS-OtomatikRandevu.new \
    && { [ -f MHRS-OtomatikRandevu ] && cp MHRS-OtomatikRandevu MHRS-OtomatikRandevu.eski || true; } \
    && mv -f MHRS-OtomatikRandevu.new MHRS-OtomatikRandevu"

echo "==> $SVC yeniden başlatılıyor"
ssh "${SSH_OPTS[@]}" "$REMOTE" "systemctl restart '$SVC'"
sleep 20
echo "==> Sağlık kontrolü"
ssh "${SSH_OPTS[@]}" "$REMOTE" "systemctl is-active '$SVC'; journalctl -u '$SVC' -n 12 --no-pager -o cat"
echo
echo "Geri almak için sunucuda: cd $DIR && mv MHRS-OtomatikRandevu.eski MHRS-OtomatikRandevu && systemctl restart $SVC"
