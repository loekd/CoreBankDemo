#!/bin/bash

echo "🧹 Resetting Core Banking Demo..."
echo "================================="
echo ""

# Remove database files
echo "🗑️  Removing database files..."
rm -f CoreBankDemo.PaymentsAPI/payments.db*
rm -f CoreBankDemo.CoreBankAPI/corebank.db*

echo "✅ Demo reset complete!"
echo ""
echo "Database files removed. Restart the APIs to recreate clean databases."
