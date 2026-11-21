#!/usr/bin/env bash
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/FileodonDistributionListener"
SERVICE_NAME="${SERVICE_NAME:-fileodon-distribution-listener}"
INSTALL_DIR="${INSTALL_DIR:-/opt/fileodon-distribution-listener}"
PUBLIC_KEY_URL="${PUBLIC_KEY_URL:-https://api.fileodon.dev/api/organization/sftpconnection/pam/publickey}"
PUBLIC_KEY_PATH="${PUBLIC_KEY_PATH:-$INSTALL_DIR/public.pem}"
LISTEN_PORT="${LISTEN_PORT:-5096}"
SERVICE_USER="${SERVICE_USER:-fileodon}"

command -v dotnet >/dev/null || { echo "dotnet is required." >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemctl is required." >&2; exit 1; }

id "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"

rm -rf "$INSTALL_DIR"
dotnet publish "$PROJECT_DIR" \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o "$INSTALL_DIR"

mkdir -p "$(dirname "$PUBLIC_KEY_PATH")"
curl --fail --silent --show-error --location \
  --connect-timeout 10 --max-time 60 --retry 3 \
  --output "$PUBLIC_KEY_PATH" "$PUBLIC_KEY_URL"

grep -Eq -- '-----BEGIN ([A-Z ]*)PUBLIC KEY-----' "$PUBLIC_KEY_PATH" \
  || { echo "The public-key endpoint did not return a PEM public key." >&2; exit 1; }

cat > "$INSTALL_DIR/appsettings.Production.json" <<EOF
{
  "Urls": "http://0.0.0.0:${LISTEN_PORT}",
  "Jwt": {
    "PublicKeyPath": "${PUBLIC_KEY_PATH}"
  }
}
EOF

chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chmod 0755 "$INSTALL_DIR"
chmod 0644 "$PUBLIC_KEY_PATH"

cat > "/etc/systemd/system/${SERVICE_NAME}.service" <<EOF
[Unit]
Description=Fileodon SFTP Distribution Listener
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
ExecStart=/usr/bin/dotnet $INSTALL_DIR/FileodonDistributionListener.dll
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"
systemctl status "$SERVICE_NAME" --no-pager --lines=20

echo
echo "Installed ${SERVICE_NAME} on port ${LISTEN_PORT}."
echo "Health: http://<server>:${LISTEN_PORT}/health"
