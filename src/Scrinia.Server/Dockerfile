FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS restore
WORKDIR /build
COPY Directory.Build.targets ./
COPY src/Scrinia.Core/Scrinia.Core.csproj src/Scrinia.Core/
COPY src/Scrinia.Mcp/Scrinia.Mcp.csproj src/Scrinia.Mcp/
COPY src/Scrinia.Plugin.Abstractions/Scrinia.Plugin.Abstractions.csproj src/Scrinia.Plugin.Abstractions/
COPY src/Scrinia.Server/Scrinia.Server.csproj src/Scrinia.Server/
RUN dotnet restore src/Scrinia.Server/Scrinia.Server.csproj

FROM node:22-slim AS web-build
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ .
RUN npm run build

FROM restore AS build
COPY src/Scrinia.Core/ src/Scrinia.Core/
COPY src/Scrinia.Mcp/ src/Scrinia.Mcp/
COPY src/Scrinia.Plugin.Abstractions/ src/Scrinia.Plugin.Abstractions/
COPY src/Scrinia.Server/ src/Scrinia.Server/
COPY --from=web-build /web/dist src/Scrinia.Server/wwwroot/
RUN dotnet build src/Scrinia.Server/Scrinia.Server.csproj -c Release --no-restore

FROM build AS publish
RUN dotnet publish src/Scrinia.Server/Scrinia.Server.csproj -c Release --no-build -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
RUN groupadd -r scrinia && useradd -r -g scrinia -m scrinia
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /data && chown -R scrinia:scrinia /data
ENV ASPNETCORE_URLS=http://+:8080
ENV Scrinia__DataDir=/data
USER scrinia
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Scrinia.Server.dll"]
