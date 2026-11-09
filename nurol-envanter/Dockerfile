# syntax=docker/dockerfile:1
# Nurol Envanter: API + Blazor WebAssembly arayüzü tek imajda.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Kurum ağı HTTPS trafiğini kendi sertifikasıyla denetliyorsa (SSL inspection) kurum kök sertifikaları
# sertifikalar/ klasörüne .crt olarak konur; derleme NuGet'e bunlarla bağlanır. Klasör boşsa etkisi yoktur.
COPY sertifikalar/ /usr/local/share/ca-certificates/kurum/
RUN update-ca-certificates
WORKDIR /src
COPY Directory.Build.props Nurol.Envanter.slnx ./
COPY src/Nurol.Envanter.Shared/Nurol.Envanter.Shared.csproj src/Nurol.Envanter.Shared/
COPY src/Nurol.Envanter.Web/Nurol.Envanter.Web.csproj src/Nurol.Envanter.Web/
COPY src/Nurol.Envanter.Server/Nurol.Envanter.Server.csproj src/Nurol.Envanter.Server/
RUN dotnet restore src/Nurol.Envanter.Server/Nurol.Envanter.Server.csproj
COPY src/ src/
RUN dotnet publish src/Nurol.Envanter.Server/Nurol.Envanter.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0 \
    TZ=Europe/Istanbul
# Aynı kurum sertifikaları çalışma imajına da eklenir (kurum içi LDAPS, S3, SMTP sunucuları için).
COPY sertifikalar/ /usr/local/share/ca-certificates/kurum/
RUN update-ca-certificates
# libgssapi-krb5-2: Npgsql Kerberos/GSSAPI desteği (kurumsal PostgreSQL kimlik doğrulaması).
# libldap2: Active Directory (LDAPS) bağlantısı için System.DirectoryServices.Protocols'ün kullandığı OpenLDAP kütüphanesi.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 libldap2 \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
# Resmi imajdaki root olmayan kullanıcı (UID 1654).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nurol.Envanter.Server.dll"]
