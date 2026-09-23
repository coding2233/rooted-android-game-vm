param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $projectRoot 'artifacts'
$launcherOutput = Join-Path $artifacts 'publish\Launcher'
$setupOutput = Join-Path $artifacts 'publish\Setup'
$cliOutput = Join-Path $artifacts 'publish\Cli'
$releaseDirectory = Join-Path $artifacts 'release'
$innoScript = Join-Path $projectRoot 'installer\RootedAndroidGameVM.iss'
$innoCompiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

if (-not (Test-Path -LiteralPath $innoCompiler)) {
    throw "Inno Setup compiler was not found at $innoCompiler"
}

Write-Output 'Running unit tests...'
dotnet test (Join-Path $projectRoot 'RootedAndroidGameVM.sln') -c $Configuration --filter 'Category!=LocalIntegration&Category!=CleanE2E'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

Write-Output 'Publishing executables...'
dotnet publish (Join-Path $projectRoot 'src\RootedAndroidGameVM.Launcher\RootedAndroidGameVM.Launcher.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $launcherOutput
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
dotnet publish (Join-Path $projectRoot 'src\RootedAndroidGameVM.Setup\RootedAndroidGameVM.Setup.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $setupOutput
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }
dotnet publish (Join-Path $projectRoot 'src\RootedAndroidGameVM.Cli\RootedAndroidGameVM.Cli.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $cliOutput
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

if (Test-Path -LiteralPath $releaseDirectory) {
    Get-ChildItem -LiteralPath $releaseDirectory -File | ForEach-Object { [IO.File]::Delete($_.FullName) }
}

Write-Output 'Compiling the installer...'
& $innoCompiler $innoScript
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$installer = Get-ChildItem -LiteralPath $releaseDirectory -Filter 'RootedAndroidGameVM-Setup-*-x64.exe' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $installer) { throw 'Installer was not produced.' }
$unsignedPath = Join-Path $releaseDirectory ([IO.Path]::GetFileNameWithoutExtension($installer.Name) + '-UNSIGNED.exe')
[IO.File]::Move($installer.FullName, $unsignedPath, $true)
$digest = (Get-FileHash -LiteralPath $unsignedPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($unsignedPath + '.sha256') -Value "$digest  $([IO.Path]::GetFileName($unsignedPath))" -Encoding ascii
Write-Output "Prerelease installer: $unsignedPath"
