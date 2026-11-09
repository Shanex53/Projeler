# Windows'un güvendiği kök ve ara sertifikaları sertifikalar\ klasörüne .crt (PEM) olarak yazar.
# Kurum ağı SSL denetimi yapıyorsa Docker derlemesinin NuGet'e bağlanabilmesi için gerekir.
# Kullanım (nurol-envanter klasöründe):  powershell -ExecutionPolicy Bypass -File .\sertifika-aktar.ps1
$ErrorActionPreference = "Stop"
$hedef = Join-Path $PSScriptRoot "sertifikalar"
New-Item -ItemType Directory -Force -Path $hedef | Out-Null
Get-ChildItem $hedef -Filter "*.crt" | Remove-Item
$simdi = Get-Date
$sayi = 0
foreach ($depo in "Cert:\LocalMachine\Root", "Cert:\LocalMachine\CA", "Cert:\CurrentUser\Root", "Cert:\CurrentUser\CA") {
    foreach ($s in Get-ChildItem $depo -ErrorAction SilentlyContinue) {
        if ($s.NotAfter -lt $simdi) { continue }
        $dosya = Join-Path $hedef ("kurum-" + $s.Thumbprint + ".crt")
        if (Test-Path $dosya) { continue }
        $pem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($s.RawData, "InsertLineBreaks").Replace("`r`n", "`n") + "`n-----END CERTIFICATE-----`n"
        [IO.File]::WriteAllText($dosya, $pem, [Text.Encoding]::ASCII)
        $sayi++
    }
}
Write-Host "$sayi sertifika $hedef klasörüne yazıldı. Şimdi derlemeyi yeniden başlatın:"
Write-Host "  docker compose -f deploy/docker-compose.yml up --build"
