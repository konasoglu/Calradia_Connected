[CmdletBinding()]
param(
    [string]$ReleaseNotes = "Automated release package containing CalradiaConnected Mod and Server Manager.",
    [string]$BannerlordGameDir = "",
    [switch]$SkipPushTag
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$versionConfigPath = Join-Path $repoRoot "version.json"
if (-not (Test-Path $versionConfigPath)) {
    throw "version.json bulunamadi: $versionConfigPath"
}

$meta = Get-Content $versionConfigPath -Raw | ConvertFrom-Json
$version = $meta.version
$tag = $meta.tag
$repository = $meta.repository
$modAssetName = $meta.modAssetName
$mgrAssetName = $meta.serverManagerAssetName

# Resolve dotnet executable
$localSdk = Join-Path (Split-Path -Parent $repoRoot) ".dotnet_sdk\dotnet.exe"
$dotnetExe = if (Test-Path $localSdk) { $localSdk } else { "dotnet" }

Write-Host "=== 1. Building CalradiaConnected.sln ($tag) ==="
$buildArgs = @("build", (Join-Path $repoRoot "CalradiaConnected.sln"), "-c", "Release", "-p:Platform=x64")
if (-not [string]::IsNullOrWhiteSpace($BannerlordGameDir)) {
    $buildArgs += "-p:BannerlordGameDir=$BannerlordGameDir"
}
& $dotnetExe @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

Write-Host "=== 2. Running CalradiaConnected.Verification ==="
$verifyProj = Join-Path $repoRoot "tools\CalradiaConnected.Verification\CalradiaConnected.Verification.csproj"
& $dotnetExe build $verifyProj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Verification build failed with exit code $LASTEXITCODE" }
if (-not [string]::IsNullOrWhiteSpace($BannerlordGameDir)) {
    $env:BANNERLORD_DIR = $BannerlordGameDir
}
$verifyExe = Join-Path $repoRoot "tools\CalradiaConnected.Verification\bin\x64\Release\net472\CalradiaConnected.Verification.exe"
& $verifyExe
if ($LASTEXITCODE -ne 0) { throw "Verification failed with exit code $LASTEXITCODE" }

Write-Host "=== 3. Packaging Release Assets ==="
$distDir = Join-Path $repoRoot "dist"
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$excludedExts = @(".pdb", ".log", ".tmp", ".sav")
$excludedNames = @("config.json", "users.json", "coop_settings.json", "starter_applied.json")

$modSourceDir = Join-Path $repoRoot "CalradiaConnected"
if (-not (Test-Path (Join-Path $modSourceDir "SubModule.xml"))) {
    throw "Build mod output not found at $modSourceDir"
}
$modStageDir = Join-Path $distDir "mod_stage\CalradiaConnected"
New-Item -ItemType Directory -Path $modStageDir -Force | Out-Null
Get-ChildItem -Path $modSourceDir -Recurse -File | Where-Object {
    $_.Extension -notin $excludedExts -and $_.Name -notin $excludedNames
} | ForEach-Object {
    $rel = $_.FullName.Substring($modSourceDir.Length).TrimStart('\', '/')
    $dst = Join-Path $modStageDir $rel
    $parent = Split-Path $dst -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item $_.FullName -Destination $dst -Force
}
$modZipPath = Join-Path $distDir $modAssetName
Compress-Archive -Path $modStageDir -DestinationPath $modZipPath -CompressionLevel Optimal -Force
Remove-Item (Join-Path $distDir "mod_stage") -Recurse -Force

$mgrBinDir = Join-Path $repoRoot "src\CalradiaConnected.ServerManager\bin\x64\Release\net8.0-windows"
if (-not (Test-Path $mgrBinDir)) {
    $mgrBinDir = Join-Path $repoRoot "src\CalradiaConnected.ServerManager\bin\Release\net8.0-windows"
}
if (-not (Test-Path (Join-Path $mgrBinDir "CalradiaConnected.ServerManager.exe"))) {
    throw "ServerManager build output not found at $mgrBinDir"
}

$mgrStageDir = Join-Path $distDir "mgr_stage"
New-Item -ItemType Directory -Path $mgrStageDir -Force | Out-Null
Get-ChildItem -Path $mgrBinDir -Recurse -File | Where-Object {
    $_.Extension -notin $excludedExts -and $_.Name -notin $excludedNames
} | ForEach-Object {
    $rel = $_.FullName.Substring($mgrBinDir.Length).TrimStart('\', '/')
    $dst = Join-Path $mgrStageDir $rel
    $parent = Split-Path $dst -Parent
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item $_.FullName -Destination $dst -Force
}
$mgrZipPath = Join-Path $distDir $mgrAssetName
Compress-Archive -Path "$mgrStageDir\*" -DestinationPath $mgrZipPath -CompressionLevel Optimal -Force
Remove-Item $mgrStageDir -Recurse -Force

Write-Host "Created: $modZipPath ($([Math]::Round((Get-Item $modZipPath).Length / 1MB, 2)) MB)"
Write-Host "Created: $mgrZipPath ($([Math]::Round((Get-Item $mgrZipPath).Length / 1MB, 2)) MB)"

if (-not $SkipPushTag) {
    Write-Host "=== 4. Pushing git tag $tag ==="
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    git tag -f $tag 2>&1 | Out-Host
    $tagExit = $LASTEXITCODE
    git push origin $tag --force 2>&1 | Out-Host
    $pushExit = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    if ($tagExit -ne 0 -or $pushExit -ne 0) { throw "Failed to push tag $tag" }
}

Write-Host "=== 5. Publishing GitHub Release ($repository @ $tag) ==="
$credTemp = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($credTemp, "protocol=https`nhost=github.com`n`n", [System.Text.Encoding]::ASCII)
    $credOut = cmd /c "git credential fill < `"$credTemp`""
}
finally {
    if (Test-Path $credTemp) { Remove-Item $credTemp -Force -ErrorAction SilentlyContinue }
}
$pwLine = ($credOut | Where-Object { $_ -like "password=*" } | Select-Object -First 1)
if (-not $pwLine) {
    throw "Git Credential Manager did not return a GitHub credential for host=github.com."
}
$token = $pwLine.Substring(9).Trim()
$apiHeaders = @{
    Authorization = "Bearer $token"
    Accept        = "application/vnd.github+json"
    "User-Agent"  = "CalradiaConnected-ReleasePublisher"
}

$releaseObj = $null
try {
    $releaseObj = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/tags/$tag" -Headers $apiHeaders -Method Get
}
catch {
    $releaseObj = $null
}

if ($null -eq $releaseObj) {
    $payload = @{
        tag_name   = $tag
        name       = "Calradia Connected $tag"
        body       = $ReleaseNotes
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json -Depth 4
    $releaseObj = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases" -Headers $apiHeaders -Method Post -Body $payload -ContentType "application/json; charset=utf-8"
}
else {
    $payload = @{
        name = "Calradia Connected $tag"
        body = $ReleaseNotes
    } | ConvertTo-Json -Depth 4
    $releaseObj = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/$($releaseObj.id)" -Headers $apiHeaders -Method Patch -Body $payload -ContentType "application/json; charset=utf-8"
}

$uploadBase = ($releaseObj.upload_url -replace '\{.*\}$', '')
foreach ($assetPath in @($modZipPath, $mgrZipPath)) {
    $fileName = [System.IO.Path]::GetFileName($assetPath)
    $existingAsset = $releaseObj.assets | Where-Object { $_.name -eq $fileName } | Select-Object -First 1
    if ($null -ne $existingAsset) {
        Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/assets/$($existingAsset.id)" -Headers $apiHeaders -Method Delete | Out-Null
    }

    $uploadUri = "${uploadBase}?name=$([Uri]::EscapeDataString($fileName))"
    $bytes = [System.IO.File]::ReadAllBytes($assetPath)
    $uploaded = Invoke-RestMethod -Uri $uploadUri -Headers $apiHeaders -Method Post -Body $bytes -ContentType "application/zip"
    Write-Host "Uploaded asset: $($uploaded.name) ($($uploaded.size) bytes)"
}

Write-Host "Release published cleanly: $($releaseObj.html_url)"
