#!/bin/bash
set -e

# Install Aspire CLI (idempotent - checks if already installed)
if ! command -v aspire &> /dev/null; then
  echo "Installing Aspire CLI..."
  curl -sSL https://aspire.dev/install.sh | bash
else
  echo "Aspire CLI already installed, skipping."
fi

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

# Install Dapr CLI (idempotent - checks if already installed)
if ! command -v dapr &> /dev/null; then
  echo "Installing Dapr CLI..."
  DAPR_VERSION=$(curl -s https://api.github.com/repos/dapr/cli/releases/latest | grep '"tag_name"' | cut -d'"' -f4 | sed 's/v//')
  if [ -n "$DAPR_VERSION" ]; then
    curl -sL "https://github.com/dapr/cli/releases/download/v${DAPR_VERSION}/dapr_linux_amd64.tar.gz" | sudo tar -xz -C /usr/local/bin/
    sudo chmod +x /usr/local/bin/dapr
  else
    echo "WARNING: Could not resolve Dapr version (GitHub API blocked?). Skipping Dapr install."
  fi
else
  echo "Dapr CLI already installed, skipping."
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
echo "Setting up HTTPS dev certificate..."
if [ -f "/home/vscode/.aspnet/https-host/aspnetapp.pfx" ]; then
  dotnet dev-certs https --import /home/vscode/.aspnet/https-host/aspnetapp.pfx --password "" || true
else
  dotnet dev-certs https --trust || true
fi

# Export and trust system-wide (only if cert file is missing or stale)
if [ ! -f "/usr/local/share/ca-certificates/aspnet-dev.crt" ]; then
  dotnet dev-certs https -ep /tmp/aspnet-dev.crt --format PEM
  sudo cp /tmp/aspnet-dev.crt /usr/local/share/ca-certificates/aspnet-dev.crt
  sudo update-ca-certificates
else
  echo "System dev certificate already present, skipping."
fi