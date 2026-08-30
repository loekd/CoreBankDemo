#!/bin/bash
set -e

# Install DevProxy (idempotent - checks if already installed)
if ! command -v devproxy &> /dev/null; then
  echo "Installing DevProxy..."
  curl -sL https://aka.ms/devproxy/setup.sh -o /tmp/devproxy-setup.sh
  cd /home/vscode
  echo "y" | bash /tmp/devproxy-setup.sh v1.1.0
  sudo ln -sf /home/vscode/devproxy/devproxy /usr/local/bin/devproxy
  cd -
else
  echo "DevProxy already installed, skipping."
fi


# Initialize Dapr runtime (downloads daprd and other binaries)
echo "Checking Dapr runtime..."
if [ ! -f "/home/vscode/.dapr/bin/daprd" ]; then
  echo "Initializing Dapr runtime..."
  dapr init --slim
else
  echo "Dapr runtime already initialized, skipping."
fi

dotnet restore
dotnet tool restore

# Dev certificate setup
echo "Setting up HTTPS development certificate..."

HOST_PFX="/home/vscode/.aspnet/https-host/localhost-sbx.pfx"
PASSWORD_FILE="/home/vscode/.aspnet/https-host/localhost-sbx.password"
SYSTEM_CERT="/usr/local/share/ca-certificates/aspnet-dev.crt"
TEMP_CERT="$(mktemp)"

if [ ! -r "$HOST_PFX" ] || [ ! -r "$PASSWORD_FILE" ]; then
  echo "Sandbox HTTPS certificate or password file is missing." >&2
  exit 1
fi

CERT_PASSWORD="$(cat "$PASSWORD_FILE")"

# Make the mounted Mac certificate the certificate used by Kestrel/Aspire.
dotnet dev-certs https \
  --clean \
  --import "$HOST_PFX" \
  --password "$CERT_PASSWORD"

unset CERT_PASSWORD

# Extract only the public certificate from that exact PFX.
openssl pkcs12 \
  -in "$HOST_PFX" \
  -passin "file:$PASSWORD_FILE" \
  -clcerts \
  -nokeys |
  openssl x509 \
    -outform PEM \
    -out "$TEMP_CERT"

# Trust it for Linux service-to-service traffic, including Aspire's
# Dashboard -> AppHost gRPC connection.
sudo install -m 0644 "$TEMP_CERT" "$SYSTEM_CERT"
rm -f "$TEMP_CERT"

# --fresh ensures a renewed Mac certificate replaces the previous one.
sudo update-ca-certificates --fresh

# Fail postCreate if Linux still doesn't trust the certificate.
openssl verify \
  -CAfile /etc/ssl/certs/ca-certificates.crt \
  "$SYSTEM_CERT"

echo "HTTPS development certificate imported and trusted."