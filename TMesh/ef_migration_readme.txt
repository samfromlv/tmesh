cd TBot
dotnet ef migrations add InitialCreate --context TBotDbContext --output-dir Migrations/Primary
dotnet ef migrations add InitialCreate --context AnalyticsDbContext --output-dir Migrations/Analytics