param(
    [ValidateSet("x64")]
    [string]$Architecture = "x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$engineDirectory = Join-Path $repositoryRoot "src\UnifiedAudio.EngineHost"
$engineBuild = Join-Path $engineDirectory "build"
$solution = Join-Path $repositoryRoot "UnifiedAudio.slnx"
$appProject = Join-Path $repositoryRoot "src\UnifiedAudio.App\UnifiedAudio.App.csproj"
$testProject = Join-Path $repositoryRoot "tests\UnifiedAudio.Core.Tests\UnifiedAudio.Core.Tests.csproj"
$payload = Join-Path $repositoryRoot "installer\payload"
$artifacts = Join-Path $repositoryRoot "artifacts"

$cmakeCandidates = @(@(
    (Get-Command cmake -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
if ($cmakeCandidates.Count -eq 0) { throw "CMake was not found." }
$cmake = $cmakeCandidates[0]

$env:CMAKE_GENERATOR_PLATFORM = $null
& $cmake --build $engineBuild --config Release --target UnifiedAudioEngineHost UnifiedAudioEngineTests
if ($LASTEXITCODE -ne 0) { throw "Native Release build failed." }

dotnet build $solution -c Release
if ($LASTEXITCODE -ne 0) { throw ".NET Release build failed." }

if (-not $SkipTests) {
    & (Join-Path $engineBuild "UnifiedAudioEngineTests_artefacts\Release\UnifiedAudioEngineTests.exe")
    if ($LASTEXITCODE -ne 0) { throw "Native tests failed." }
    dotnet test $testProject -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw ".NET tests failed." }
}

if (Test-Path -LiteralPath $payload) {
    $resolvedPayload = (Resolve-Path -LiteralPath $payload).Path
    $expectedPayload = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "installer\payload"))
    if (-not [string]::Equals($resolvedPayload, $expectedPayload, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear an unexpected publish directory: $resolvedPayload"
    }
    Remove-Item -LiteralPath $resolvedPayload -Recurse -Force
}
New-Item -ItemType Directory -Path $payload -Force | Out-Null
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

dotnet publish $appProject -c Release -r "win-$Architecture" --self-contained true -p:SelfContained=true -o $payload --no-restore
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$runtimeConfigPath = Join-Path $payload "UnifiedAudio.runtimeconfig.json"
$coreClrPath = Join-Path $payload "coreclr.dll"
if (-not (Test-Path -LiteralPath $runtimeConfigPath) -or -not (Test-Path -LiteralPath $coreClrPath)) {
    throw "Publish output is missing the bundled .NET runtime."
}
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
if ($null -ne $runtimeConfig.runtimeOptions.framework) {
    throw "Publish output is framework-dependent; refusing to build the installer."
}

$makeNsis = Get-Command makensis -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
if (-not $makeNsis) {
    $candidate = "C:\Program Files (x86)\NSIS\makensis.exe"
    if (Test-Path -LiteralPath $candidate) { $makeNsis = $candidate }
}
if (-not $makeNsis) { throw "NSIS makensis was not found." }

Push-Location (Join-Path $repositoryRoot "installer")
try {
    & $makeNsis "UnifiedAudio.nsi"
    if ($LASTEXITCODE -ne 0) { throw "NSIS failed." }
}
finally {
    Pop-Location
}

$installer = Get-ChildItem -LiteralPath $artifacts -Filter "UnifiedAudioSetup-*-x64.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $installer) { throw "Installer output was not produced." }
$hash = Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256
$hashFile = $installer.FullName + ".sha256"
Set-Content -LiteralPath $hashFile -Value "$($hash.Hash) *$($installer.Name)" -Encoding ascii
[pscustomobject]@{
    Installer = $installer.FullName
    Bytes = $installer.Length
    SHA256 = $hash.Hash
    SHA256File = $hashFile
}
