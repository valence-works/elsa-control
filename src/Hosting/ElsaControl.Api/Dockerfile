FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.300 AS verification-tools
ARG TARGETARCH
# Both supported artifacts and the trust root are content-pinned. Updating the verifier
# requires an explicit dependency review; the runtime never downloads executable code.
ADD --checksum=sha256:4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71 \
    https://github.com/sigstore/cosign/releases/download/v3.1.3/cosign-linux-amd64 /tmp/cosign-amd64
ADD --checksum=sha256:c5d324e091826b0d7a78eb16fef316450b4eb9aaec045611c08ba06f5e73220a \
    https://github.com/sigstore/cosign/releases/download/v3.1.3/cosign-linux-arm64 /tmp/cosign-arm64
ADD --checksum=sha256:6494e21ea73fa7ee769f85f57d5a3e6a08725eae1e38c755fc3517c9e6bc0b66 \
    https://raw.githubusercontent.com/sigstore/root-signing/c9bda74ad2221f938f7d2e0295ca3aad2da710a8/targets/trusted_root.json /opt/elsa-control/verification/trusted-root.json
RUN case "$TARGETARCH" in amd64|arm64) ;; *) exit 1 ;; esac \
    && install -m 0755 "/tmp/cosign-$TARGETARCH" /opt/elsa-control/verification/cosign \
    && chmod 0644 /opt/elsa-control/verification/trusted-root.json

FROM node:22-alpine AS console-build
WORKDIR /src/src/Hosting/ElsaControl.Console

COPY src/Hosting/ElsaControl.Console/package*.json ./
RUN npm ci

COPY src/Hosting/ElsaControl.Console/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

COPY . .
RUN mkdir -p src/Hosting/ElsaControl.Api/wwwroot/admin
COPY --from=console-build /src/src/Hosting/ElsaControl.Console/dist/ src/Hosting/ElsaControl.Api/wwwroot/admin/
RUN dotnet restore src/Hosting/ElsaControl.Api/ElsaControl.Api.csproj
# The console is built by the node stage above and copied into wwwroot/admin, and this SDK image has
# no npm, so the project's own console build is skipped here.
RUN dotnet publish src/Hosting/ElsaControl.Api/ElsaControl.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    -p:SkipAdminConsoleBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime

# The managed-instance Azure worker is intentionally in-process for the initial
# stamp. Keep every executable in the provider authority immutable: Azure CLI
# and the .NET runtime are digest-pinned multi-platform images, while sqlcmd is
# checksum-pinned per target architecture.
FROM mcr.microsoft.com/azure-cli:2.77.0@sha256:d41153958a61392a4c9db1591e33b533ca8ab8b59529d212d06bb6ec7e9c1f25 AS final
ARG TARGETARCH
ARG SQLCMD_VERSION=1.10.0
ARG SQLCMD_AMD64_SHA256=92516d98c63d99b0994de5b61350c91f6915f9b76f139a59039fbcb225c2e987
ARG SQLCMD_ARM64_SHA256=9faaa981f9c374f319ac796dedb4678499b8596c87d5b6c512e9b0e7a3b74f8e
ARG PULUMI_VERSION=3.278.0
ARG PULUMI_AMD64_SHA256=bc1b25c6ae82793ba1f0b890b2a6c0d380652694e42e8a2556736071eda9f583
ARG PULUMI_ARM64_SHA256=35de8596e629caf1556ac7560cdbd5b09a07a096dda0e0dd035d09bb5a3287cb
# Copying the shared .NET runtime does not copy its OS dependencies. Keep full
# globalization/time-zone support; invariant mode is not compatible with SQL.
RUN tdnf install --assumeyes tar bzip2 icu tzdata libstdc++ openssl-libs \
    && tdnf clean all \
    && case "$TARGETARCH" in \
      amd64) sqlcmd_sha256="$SQLCMD_AMD64_SHA256" ;; \
      arm64) sqlcmd_sha256="$SQLCMD_ARM64_SHA256" ;; \
      *) echo "Unsupported target architecture" >&2; exit 1 ;; \
    esac \
    && curl --fail --silent --show-error --location \
      "https://github.com/microsoft/go-sqlcmd/releases/download/v${SQLCMD_VERSION}/sqlcmd-linux-${TARGETARCH}.tar.bz2" \
      --output /tmp/sqlcmd.tar.bz2 \
    && echo "${sqlcmd_sha256}  /tmp/sqlcmd.tar.bz2" | sha256sum --check --status \
    && tar --extract --bzip2 --file /tmp/sqlcmd.tar.bz2 --directory /usr/local/bin sqlcmd \
    && chmod 0755 /usr/local/bin/sqlcmd \
    && command -v az >/dev/null \
    && command -v curl >/dev/null \
    && /usr/local/bin/sqlcmd --version >/dev/null \
    && rm /tmp/sqlcmd.tar.bz2 \
    && case "$TARGETARCH" in \
      amd64) pulumi_sha256="$PULUMI_AMD64_SHA256"; pulumi_arch="x64" ;; \
      arm64) pulumi_sha256="$PULUMI_ARM64_SHA256"; pulumi_arch="arm64" ;; \
      *) echo "Unsupported target architecture" >&2; exit 1 ;; \
    esac \
    && curl --fail --silent --show-error --location \
      "https://github.com/pulumi/pulumi/releases/download/v${PULUMI_VERSION}/pulumi-v${PULUMI_VERSION}-linux-${pulumi_arch}.tar.gz" \
      --output /tmp/pulumi.tar.gz \
    && echo "${pulumi_sha256}  /tmp/pulumi.tar.gz" | sha256sum --check --status \
    && tar --extract --gzip --file /tmp/pulumi.tar.gz --directory /tmp \
    && install -m 0755 /tmp/pulumi/pulumi /tmp/pulumi/pulumi-language-dotnet /usr/local/bin/ \
    && /usr/local/bin/pulumi version >/dev/null \
    && rm -rf /tmp/pulumi.tar.gz /tmp/pulumi

ARG PULUMI_AZURE_NATIVE_VERSION=3.11.0
ARG PULUMI_AZURE_NATIVE_AMD64_SHA256=751f45b836c65b50860e145a68b74a92830d095fbaca75de7286d9c17aa904de
ARG PULUMI_AZURE_NATIVE_ARM64_SHA256=eea199477b87743eed916c285d53879b3ae8c722b685602d6f350bb49d816cd1
ENV PULUMI_HOME=/opt/pulumi \
    PULUMI_SKIP_UPDATE_CHECK=true \
    PULUMI_DISABLE_AUTOMATIC_PLUGIN_ACQUISITION=true
RUN case "$TARGETARCH" in \
      amd64) plugin_sha256="$PULUMI_AZURE_NATIVE_AMD64_SHA256"; plugin_arch="amd64" ;; \
      arm64) plugin_sha256="$PULUMI_AZURE_NATIVE_ARM64_SHA256"; plugin_arch="arm64" ;; \
      *) echo "Unsupported target architecture" >&2; exit 1 ;; \
    esac \
    && curl --fail --silent --show-error --location \
      "https://github.com/pulumi/pulumi-azure-native/releases/download/v${PULUMI_AZURE_NATIVE_VERSION}/pulumi-resource-azure-native-v${PULUMI_AZURE_NATIVE_VERSION}-linux-${plugin_arch}.tar.gz" \
      --output /tmp/azure-native.tar.gz \
    && echo "${plugin_sha256}  /tmp/azure-native.tar.gz" | sha256sum --check --status \
    && mkdir -p /opt/pulumi/plugins/resource-azure-native-v${PULUMI_AZURE_NATIVE_VERSION} \
    && tar --extract --gzip --file /tmp/azure-native.tar.gz --directory /opt/pulumi/plugins/resource-azure-native-v${PULUMI_AZURE_NATIVE_VERSION} \
    && chmod 0755 /opt/pulumi/plugins/resource-azure-native-v${PULUMI_AZURE_NATIVE_VERSION}/pulumi-resource-azure-native \
    && rm /tmp/azure-native.tar.gz

ARG PULUMI_RANDOM_VERSION=4.18.5
ARG PULUMI_RANDOM_AMD64_SHA256=2f0e2e420022186f5ae94e0b84ee78ce2f6c9b9d7bd2e72995f8f95d03ac9137
ARG PULUMI_RANDOM_ARM64_SHA256=d82e69672cc9d89b2ab23ce23754cdb5abeb18a8462df07ba46107fa078762a3
RUN case "$TARGETARCH" in \
      amd64) plugin_sha256="$PULUMI_RANDOM_AMD64_SHA256"; plugin_arch="amd64" ;; \
      arm64) plugin_sha256="$PULUMI_RANDOM_ARM64_SHA256"; plugin_arch="arm64" ;; \
      *) echo "Unsupported target architecture" >&2; exit 1 ;; \
    esac \
    && curl --fail --silent --show-error --location \
      "https://github.com/pulumi/pulumi-random/releases/download/v${PULUMI_RANDOM_VERSION}/pulumi-resource-random-v${PULUMI_RANDOM_VERSION}-linux-${plugin_arch}.tar.gz" \
      --output /tmp/random.tar.gz \
    && echo "${plugin_sha256}  /tmp/random.tar.gz" | sha256sum --check --status \
    && mkdir -p /opt/pulumi/plugins/resource-random-v${PULUMI_RANDOM_VERSION} \
    && tar --extract --gzip --file /tmp/random.tar.gz --directory /opt/pulumi/plugins/resource-random-v${PULUMI_RANDOM_VERSION} \
    && chmod 0755 /opt/pulumi/plugins/resource-random-v${PULUMI_RANDOM_VERSION}/pulumi-resource-random \
    && rm /tmp/random.tar.gz
COPY --from=runtime /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && dotnet --list-runtimes | grep --fixed-strings 'Microsoft.AspNetCore.App 10.' >/dev/null
ARG ELSA_CONTROL_IMAGE_ID=unknown
WORKDIR /app
LABEL org.opencontainers.image.title="Elsa Control" \
      org.opencontainers.image.source="https://github.com/valence-works/elsa-control" \
      org.opencontainers.image.licenses="LicenseRef-Elsa-Control-Commercial"
COPY --from=build /app/publish .
COPY --from=verification-tools /opt/elsa-control/verification /opt/elsa-control/verification
COPY LICENSE NOTICE.md ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ELSA_CONTROL_IMAGE_ID=$ELSA_CONTROL_IMAGE_ID \
    Deployment__AzureProvider__Runner__AzureCliPath=/usr/bin/az \
    Deployment__AzureProvider__Runner__SqlCmdPath=/usr/local/bin/sqlcmd \
    Deployment__AzureProvider__Runner__CurlPath=/usr/bin/curl \
    Deployment__AzureProvider__Runner__TemplateRoot=/app/azure-provider
COPY infra/azure-production/ /app/azure-provider/
ENTRYPOINT ["dotnet", "ElsaControl.Api.dll"]
