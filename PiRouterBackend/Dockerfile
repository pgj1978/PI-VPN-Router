# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine-arm64v8 AS build

WORKDIR /app

COPY PiRouterBackend.csproj .

RUN dotnet restore

COPY . .

RUN dotnet publish -c Debug -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine-arm64v8

RUN apk add --no-cache wireguard-tools iptables ip6tables iproute2

WORKDIR /app

COPY --from=build /app/out .

# Expose both application and debugger ports
EXPOSE 51508 4024

ENV ASPNETCORE_URLS=http://0.0.0.0:51508
ENV ASPNETCORE_ENVIRONMENT=Development

# Enable debugging
ENV DOTNET_DbgEnableMiniDump=1
ENV DOTNET_EnableDiagnostics=1

ENTRYPOINT ["dotnet", "PiRouterBackend.dll"]
