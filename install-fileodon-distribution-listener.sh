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
BUILD_DIR="${BUILD_DIR:-}"
SKIP_BUILD="${SKIP_BUILD:-0}"

command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemctl is required." >&2; exit 1; }

usage() {
    cat <<EOF
Usage: sudo $0 [options]

Options:
  --skip-build              Use an existing build directory instead of running dotnet publish
  --build-dir DIR           Existing publish directory (implies --skip-build)
  -h, --help                Show this help

Environment:
  INSTALL_DIR               Installation directory (default: /opt/fileodon-distribution-listener)
  BUILD_DIR                 Existing publish directory
  SKIP_BUILD                Set to 1 to use BUILD_DIR
  PUBLIC_KEY_URL            Master API public-key endpoint
  PUBLIC_KEY_PATH           Public-key destination
  LISTEN_PORT               Listener port (default: 5096)
  SERVICE_NAME              systemd service name
  SERVICE_USER              Service account name
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build)
            SKIP_BUILD=1
            shift
            ;;
        --build-dir)
            [[ $# -ge 2 ]] || { echo "Error: --build-dir requires a directory." >&2; exit 1; }
            BUILD_DIR="$2"
            SKIP_BUILD=1
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Error: unknown option '$1'" >&2
            usage >&2
            exit 1
            ;;
    esac
done

id "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"

if [[ "$SKIP_BUILD" == "1" ]]; then
    [[ -n "$BUILD_DIR" ]] || { echo "Error: --build-dir or BUILD_DIR is required with --skip-build." >&2; exit 1; }
    BUILD_DIR="$(cd -- "$BUILD_DIR" && pwd)"
    [[ -f "$BUILD_DIR/FileodonDistributionListener.dll" ]] \
        || { echo "Error: build directory '$BUILD_DIR' does not contain FileodonDistributionListener.dll." >&2; exit 1; }

    rm -rf "$INSTALL_DIR"
    mkdir -p "$INSTALL_DIR"
    cp -a "$BUILD_DIR"/. "$INSTALL_DIR"/
    echo "Using existing build directory: $BUILD_DIR"
else
    command -v dotnet >/dev/null || { echo "dotnet is required." >&2; exit 1; }

    rm -rf "$INSTALL_DIR"
    dotnet publish "$PROJECT_DIR" \
      -c Release \
      -r linux-x64 \
      --self-contained false \
      -o "$INSTALL_DIR"
fi

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
