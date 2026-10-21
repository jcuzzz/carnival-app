# API image (ADR-0001). Requires the .NET 10 SDK image at build time.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CarnivalApp.Api/CarnivalApp.Api.csproj src/CarnivalApp.Api/
RUN dotnet restore src/CarnivalApp.Api/CarnivalApp.Api.csproj
COPY . .
RUN dotnet publish src/CarnivalApp.Api/CarnivalApp.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080

# Non-root (assessment C3). The aspnet image ships user `app` = $APP_UID.
# HOME is set explicitly because DataProtection has no configured path and
# derives one from $HOME — a wrong HOME silently relocates the keyring and
# invalidates every auth cookie. The directory must exist and be owned here so
# a FRESH dp-keys volume inherits that ownership; an existing volume is
# chown'd by scripts/deploy/vm-deploy.sh.
ENV HOME=/home/app
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
 && chown -R $APP_UID:$APP_UID /home/app
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "CarnivalApp.Api.dll"]
