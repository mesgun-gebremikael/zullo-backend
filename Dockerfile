FROM mcr.microsoft.com/dotnet/sdk:8.0.427 AS build
WORKDIR /src
COPY Zullo.Api.csproj ./
# Optional public CA for builders behind a corporate/cloud HTTPS proxy.
# It stays in the SDK stage and is never copied into the runtime image.
RUN --mount=type=secret,id=build_ca \
    if [ -f /run/secrets/build_ca ]; then \
      cp /run/secrets/build_ca /usr/local/share/ca-certificates/build-proxy.crt && update-ca-certificates; \
    fi
COPY . ./
# Keep package restore, publication and the offline migration tool in one build step.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore Zullo.Api.csproj && \
    dotnet tool install --tool-path /tools dotnet-ef --version 8.0.23 && \
    dotnet publish Zullo.Api.csproj -c Release --no-restore -o /out/app && \
    ASPNETCORE_ENVIRONMENT=Staging ConnectionStrings__Default="Host=localhost;Database=design_only;Username=design_only" \
    /tools/dotnet-ef migrations bundle --configuration Release --output /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out/app ./
COPY --from=build --chmod=755 /out/efbundle ./efbundle
COPY --chmod=644 deploy/appsettings.Container.json ./appsettings.json
COPY --chmod=755 deploy/entrypoint.sh ./entrypoint.sh
USER $APP_UID
EXPOSE 10000
ENTRYPOINT ["/app/entrypoint.sh"]
