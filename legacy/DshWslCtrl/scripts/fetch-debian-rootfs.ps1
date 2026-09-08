$ErrorActionPreference = "Stop"
$iso = Join-Path $env:USERPROFILE "Downloads\debian-trixie-rootfs.tar.gz"
$repo = "library/debian"
$tag = "trixie"
$authBase = "https://auth.docker.io/token"
$reg = "https://registry-1.docker.io/v2"

# 1) token
$authResp = Invoke-RestMethod -UseBasicParsing "$authBase?service=registry.docker.io&scope=repository:$repo:pull"
$token = $authResp.token
Write-Host "token len: $($token.Length)"

# 2) manifest list (OCI index)
$headers = @{ Authorization = "Bearer $token"; Accept = "application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json" }
$ml = Invoke-RestMethod -UseBasicParsing -Headers $headers "$reg/$repo/manifests/$tag"
$amd = $ml.manifests | Where-Object { $_.platform.architecture -eq "amd64" -and $_.platform.os -eq "linux" } | Select-Object -First 1
Write-Host "amd64 manifest digest: $($amd.digest)"

# 3) 单架构 manifest
$headers2 = @{ Authorization = "Bearer $token"; Accept = "application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json" }
$m = Invoke-RestMethod -UseBasicParsing -Headers $headers2 "$reg/$repo/manifests/$($amd.digest)"
$layer = $m.layers | Select-Object -First 1
Write-Host "base layer digest: $($layer.digest)  size: $([math]::Round($layer.size/1MB,1))MB"

# 4) 下载 blob
Write-Host "下载 base layer -> $iso ..."
Invoke-WebRequest -UseBasicParsing -Headers @{ Authorization = "Bearer $token" } "$reg/$repo/blobs/$($layer.digest)" -OutFile $iso
$fi = Get-Item $iso
Write-Host "完成: $($fi.FullName)  $([math]::Round($fi.Length/1MB,1))MB"
