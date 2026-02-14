#!/bin/bash

echo "🚀 Starting Core Banking Demo with .NET Aspire"
echo "=============================================="
echo ""

# Check if docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Docker is not running. Please start Docker first."
    echo "   Aspire needs Docker to run containers (Jaeger)."
    exit 1
fi

echo "✅ Docker is running"
echo ""
echo "🏃 Starting Aspire AppHost..."
echo ""

cd CoreBankDemo.AppHost
dotnet run
