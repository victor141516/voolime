param(
    [Parameter(Mandatory = $true)][string]$AppName,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [string[]]$RuntimeIds = @('win-x64'),
    [string]$OutputDirectory = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$wkPackages = @()
$wkChecksums = @()
foreach ($wkRid in $RuntimeIds) {
    $wkPublish = Join-Path $OutputDirectory "build-$wkRid"
    dotnet publish $ProjectPath --configuration Release --runtime $wkRid --self-contained true --output $wkPublish -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publishing $AppName for $wkRid failed." }
    $wkExecutable = Join-Path $wkPublish "$AppName.exe"
    if (-not (Test-Path -LiteralPath $wkExecutable)) { throw "Missing $AppName.exe." }
    $wkArchiveName = "$AppName-$Version-$wkRid-self-contained.zip"
    $wkArchivePath = Join-Path $OutputDirectory $wkArchiveName
    Compress-Archive -LiteralPath $wkExecutable -DestinationPath $wkArchivePath -CompressionLevel Optimal -Force
    $wkHash = (Get-FileHash -LiteralPath $wkArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $wkPackages += [ordered]@{ architecture = $wkRid.Replace('win-', ''); asset = $wkArchiveName; executable = "$AppName.exe"; sha256 = $wkHash }
    $wkChecksums += "$wkHash  $wkArchiveName"
    if ($wkRid -eq 'win-x64') { Copy-Item -LiteralPath $wkExecutable -Destination (Join-Path $OutputDirectory "$AppName.exe") -Force }
}
$wkManifest = [ordered]@{ schema = 1; background = $true; singleInstance = $true; selfContained = $true; packages = $wkPackages }
$wkManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'winkit.json') -Encoding utf8
$wkChecksums | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Prepared $AppName $Version in $OutputDirectory"
