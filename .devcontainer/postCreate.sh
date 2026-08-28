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

# Dev cert setup (idempotent - dotnet dev-certs checks internally)
echo "Setting up HTTPS development certificate..."

HOST_PFX="/home/vscode/.aspnet/https-host/localhost-sbx.pfx"
PASSWORD_FILE="/home/vscode/.aspnet/https-host/localhost-sbx.password"

if [ ! -r "$HOST_PFX" ] || [ ! -r "$PASSWORD_FILE" ]; then
  echo "Sandbox HTTPS certificate or password file is missing." >&2
  exit 1
fi

CERT_PASSWORD="$(cat "$PASSWORD_FILE")"

dotnet dev-certs https \
  --clean \
  --import "$HOST_PFX" \
  --password "$CERT_PASSWORD"

unset CERT_PASSWORD

echo "HTTPS development certificate setup complete."