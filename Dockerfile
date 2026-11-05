# Nurol Envanter - konteyner imajı (Linux, .NET 10)
#   docker build -t registry.nurol.local/nurol-envanter:1.0.0 .
# Çok aşamalı: SDK ile derlenir, yalnızca çalışma zamanı + yayın çıktısı son imaja girer.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Önce yalnızca proje dosyaları: kod değişse de paket geri yükleme katmanı önbellekten gelir
COPY src/NurolEnvanter.Domain/NurolEnvanter.Domain.csproj src/NurolEnvanter.Domain/
COPY src/NurolEnvanter.Infrastructure/NurolEnvanter.Infrastructure.csproj src/NurolEnvanter.Infrastructure/
COPY src/NurolEnvanter.Web/NurolEnvanter.Web.csproj src/NurolEnvanter.Web/
RUN dotnet restore src/NurolEnvanter.Web/NurolEnvanter.Web.csproj
COPY src/ src/
# --no-restore KULLANMAYIN: Blazor'un istemci betiği (_framework/blazor.web.js) SDK'nın kaynak koda bakarak
# örtük eklediği bir paketten gelir; yalnızca .csproj ile yapılan ilk restore onu atlar ve sayfalar etkileşimsiz kalır.
RUN dotnet publish src/NurolEnvanter.Web/NurolEnvanter.Web.csproj -c Release -o /app /p:UseAppHost=false \
 && test -f /app/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# fonts-dejavu-core: PDF tutanak/raporlarda Türkçe karakterler (Segoe UI yoksa DejaVu Sans kullanılır)
# tzdata: tarih/saatler Türkiye saatiyle (uygulama yerel saat kullanır)
# libldap2: Active Directory (LDAP/LDAPS) senkronizasyonu
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu-core tzdata libldap2 \
 && rm -rf /var/lib/apt/lists/*
ENV TZ=Europe/Istanbul \
    ASPNETCORE_HTTP_PORTS=8080 \
    Logging__File=false \
    NUROL_FONT_DIR=/usr/share/fonts/truetype/dejavu
WORKDIR /app
COPY --from=build /app .
# Root olmayan kullanıcı (imajın hazır 'app' kullanıcısı, UID 1654)
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "NurolEnvanter.Web.dll"]
