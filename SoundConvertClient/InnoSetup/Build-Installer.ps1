[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$PublishDirectory = '',
    [string]$OutputDirectory = '',
    [string]$CompilerPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$version = (Get-Content -LiteralPath (Join-Path $repo 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid VERSION: $version" }
$publish = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $repo 'SoundConvertClient\artifacts\publish\win-x64' }
$publish = $publish.TrimEnd([char[]]'\/')
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Split-Path -Path $publish -Parent }
if (!$CompilerPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $CompilerPath = if ($compiler) { $compiler.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (!(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup 6.7.3+ is required; pass -CompilerPath if necessary.' }
if (!$SkipPublish) {
    # A fresh folder prevents obsolete assemblies from entering an upgrade package.
    if (Test-Path -LiteralPath $publish) { throw "Publish directory already exists. Use a new -PublishDirectory, or verify its contents and pass -SkipPublish: $publish" }
    & dotnet publish (Join-Path $repo 'SoundConvertClient\App\SoundConvertClient.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Client / Engine publish failed ($LASTEXITCODE)." }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
& $CompilerPath "/DMyPublishDir=$publish" "/DMyOutputDir=$output" (Join-Path $PSScriptRoot 'SoundConvertClient.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }
$installer = Join-Path $output "SoundConvertClient-Setup-V$version-x64.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output $installer
Write-Output "SHA256: $hash"
