cd TBot
dotnet ef migrations add InitialCreate --context TBotDbContext --output-dir Migrations/Primary
#Make sure AnalyticsPostgresConnectionString is set in appsettings.json to run analytics migration
dotnet ef migrations add InitialCreate --context AnalyticsDbContext --output-dir Migrations/Analytics