# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
USER $APP_UID
WORKDIR /tbot


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TBot.csproj", "."]
RUN dotnet restore "./TBot.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./TBot.csproj" -c $BUILD_CONFIGURATION -o /app/build
# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /tbot
# Temporarily switch to root to set up writable directories, then back to runtime user
RUN mkdir -p /tbot/app

USER root
RUN mkdir -p /tbot/data \
    && chown -R $APP_UID:0 /tbot/data \
    && chmod 775 /tbot/data
USER $APP_UID
COPY --from=publish /app/publish /tbot/app
COPY --from=publish /app/publish/appsettings.sample.json /tbot/config/appsettings.json
# Declare volumes for user-provided configuration and persistent data (SQLite db)
VOLUME ["/tbot/config", "/tbot/data"]
# Entry point; command-line args appended (e.g. /installwebhook, /checkinstallwebhook, /updatedb)
ENTRYPOINT ["dotnet", "/tbot/app/TBot.dll"]