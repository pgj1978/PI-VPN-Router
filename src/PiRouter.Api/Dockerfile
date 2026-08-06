# Build on the host's own architecture and cross-compile to the target, so this image
# builds on an x64 dev machine as well as on the Pi. The previous Dockerfile hardcoded
# -alpine-arm64v8 for both stages and therefore could only ever build on the Pi itself.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH

WORKDIR /src

COPY PiRouter.Core/PiRouter.Core.csproj PiRouter.Core/
COPY PiRouter.Api/PiRouter.Api.csproj PiRouter.Api/
RUN dotnet restore PiRouter.Api/PiRouter.Api.csproj -a "$TARGETARCH"

COPY . .

# Release, not Debug. The old image shipped a Debug build to production.
RUN dotnet publish PiRouter.Api/PiRouter.Api.csproj \
      -c Release \
      -a "$TARGETARCH" \
      --no-restore \
      -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# iptables here is xtables-nft-multi, matching the nf_tables backend Debian 12 uses on the
# host. A legacy-backend binary would write rules the host could not see.
RUN apk add --no-cache \
      wireguard-tools \
      iptables \
      ip6tables \
      iproute2 \
      conntrack-tools \
      curl \
      iputils \
      util-linux-misc

WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 51508

ENTRYPOINT ["dotnet", "PiRouter.Api.dll"]
