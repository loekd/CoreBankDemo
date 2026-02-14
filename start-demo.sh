#!/bin/bash

echo "🏦 Core Banking Demo - Startup Script"
echo "======================================"
echo ""

# Check if docker is running
if ! docker info > /dev/null 2>&1; then
    echo "❌ Docker is not running. Please start Docker first."
    exit 1
fi

# Restore tools
echo "🔧 Restoring .NET tools..."
dotnet tool restore

echo ""
echo "✅ Ready to start!"
echo ""
echo "🚀 Starting with .NET Aspire (Recommended):"
echo "   cd CoreBankDemo.AppHost && dotnet run"
echo ""
echo "   This launches everything: APIs, Jaeger, Aspire Dashboard"
echo ""
echo "🌐 URLs:"
echo "   Aspire Dashboard: http://localhost:15888 ⭐"
echo "   Jaeger UI:        http://localhost:16686"
echo "   Payments API:     http://localhost:5294"
echo "   Core Bank API:    http://localhost:5032"
echo ""
echo "🎭 For chaos testing (separate terminal):"
echo "   dotnet devproxy --config-file devproxy.json"
echo ""
echo "📖 See README.md and DEMO-GUIDE.md for full instructions"
