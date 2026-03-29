# Suggested Commands

## Running the Application
```bash
# Start everything (recommended)
cd CoreBankDemo.AppHost && dotnet run
# or
aspire run   # from AppHost directory

# Run individual service
dotnet run --project CoreBankDemo.PaymentsAPI
dotnet run --project CoreBankDemo.CoreBankAPI
```

## Building
```bash
dotnet build CoreBankDemo.sln
dotnet build --project CoreBankDemo.PaymentsAPI
```

## Testing / Load Tests
```bash
dotnet run --project CoreBankDemo.LoadTests
```

## Database
```bash
# No EF migrations in this repo (EnsureCreated() only)
# Recreate state by restarting disposable infrastructure via Aspire
```

## Troubleshooting ports
```bash
lsof -ti:5032 | xargs kill   # Kill CoreBankAPI
lsof -ti:5294 | xargs kill   # Kill PaymentsAPI
lsof -ti:8000 | xargs kill   # Kill Dev Proxy
```

## Outdated packages
```bash
dotnet outdated
```
