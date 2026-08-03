[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $artifacts.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The artifacts directory resolved outside the repository.'
}

if (Test-Path -LiteralPath $artifacts) {
    Remove-Item -LiteralPath $artifacts -Recurse -Force
}

$appOutput = Join-Path $artifacts 'app'
$releaseOutput = Join-Path $artifacts 'release'
New-Item -ItemType Directory -Path $appOutput, $releaseOutput | Out-Null

dotnet restore (Join-Path $root 'HerculesWaveBridge.sln')
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
dotnet test (Join-Path $root 'HerculesWaveBridge.sln') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
dotnet publish (Join-Path $root 'src\HerculesWaveBridge\HerculesWaveBridge.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $appOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 is required. Install package JRSoftware.InnoSetup with winget.'
}

& $iscc (Join-Path $root 'installer\HerculesWaveBridge.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
$releaseSetup = Join-Path $releaseOutput 'Hercules-Wave-Bridge-Setup.exe'
$hash = Get-FileHash -LiteralPath $releaseSetup -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $($hash.Path | Split-Path -Leaf)" |
    Set-Content -LiteralPath (Join-Path $releaseOutput 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Release artifacts: $releaseOutput"
