[CmdletBinding()]
param([string]$PicoSdk = "$env:USERPROFILE/.pico-sdk/sdk/2.3.1")
$ErrorActionPreference = 'Stop'
$board = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $board 'build/host-checks'
New-Item -ItemType Directory -Force $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ build tools were not found.' }
$msvc = Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$windowsSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdkVersion = Get-ChildItem (Join-Path $windowsSdk 'Include') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'ucrt') } | Sort-Object Name -Descending | Select-Object -First 1
$env:INCLUDE = "$(Join-Path $msvc.FullName 'include');$(Join-Path $sdkVersion.FullName 'ucrt');$(Join-Path $sdkVersion.FullName 'shared');$(Join-Path $sdkVersion.FullName 'um')"
$env:LIB = "$(Join-Path $msvc.FullName 'lib/x64');$(Join-Path $windowsSdk "Lib/$($sdkVersion.Name)/ucrt/x64");$(Join-Path $windowsSdk "Lib/$($sdkVersion.Name)/um/x64")"
$compiler = Join-Path $msvc.FullName 'bin/Hostx64/x64/cl.exe'
function Invoke-Check([string]$Name, [string[]]$CompilerArgs) {
    $exe = Join-Path $output "$Name.exe"
    & $compiler /nologo /std:c11 @CompilerArgs "/Fe:$exe" "/Fo:$output/"
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed." }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "$Name failed." }
}
Push-Location $PSScriptRoot
try {
    Invoke-Check 'indicator_host_test' @('/experimental:c11atomics', '/I', 'indicator_stubs', '/I', 'stubs', '/I', '../usb', 'indicator_host_test.c')
    Invoke-Check 'udp_ports_host_test' @('udp_ports_host_test.c')
    Invoke-Check 'usb_control_notify_host_test' @('/experimental:c11atomics', '/I', 'stubs', '/I', '../usb', 'usb_control_notify_host_test.c')
    Invoke-Check 'usb_host_control_host_test' @('/experimental:c11atomics', '/I', 'stubs', '/I', '../usb', 'usb_host_control_host_test.c')
    Invoke-Check 'usb_audio_host_test' @('/experimental:c11atomics', '/I', 'stubs', '/I', '../usb', 'usb_audio_host_test.c', '../usb/usb_control_notify.c')
    Invoke-Check 'usb_microphone_host_test' @('/experimental:c11atomics', '/I', 'stubs', '/I', '../usb', 'usb_microphone_host_test.c', '../usb/usb_control_notify.c')
    Invoke-Check 'usb_descriptor_profiles_test' @('/I', '../usb', 'usb_descriptor_profiles_test.c')
    $productionTemplate=Join-Path $output 'production-descriptor-template.bin'
    if(Test-Path -LiteralPath $productionTemplate){
        & $compiler /nologo /std:c11 /I '../usb' 'usb_production_profiles_test.c' "/Fe:$output/usb_production_profiles_test.exe" "/Fo:$output/"
        if($LASTEXITCODE -ne 0){throw 'Production profile compilation failed'}
        & (Join-Path $output 'usb_production_profiles_test.exe') $productionTemplate
        if($LASTEXITCODE -ne 0){throw 'Production ELF profile check failed'}
    }
    Invoke-Check 'boot_host_test' @('/utf-8', '/experimental:c11atomics', '/I', 'stubs', '/I', '../usb', 'boot_host_test.c', '../usb/usb_control_notify.c')
    foreach ($scenario in @('--defaults', '--bad-names', '--init-failure', '--hires')) {
        & (Join-Path $output 'boot_host_test.exe') $scenario
        if ($LASTEXITCODE -ne 0) { throw "Boot scenario $scenario failed." }
    }
    Invoke-Check 'config_store_host_test' @('/I', 'stubs', '/I', '../usb', 'config_store_host_test.c')
    Invoke-Check 'watchdog_host_test' @('/experimental:c11atomics', '/I', 'stubs', 'watchdog_host_test.c')
    Invoke-Check 'dhcp_host_test' @('/I', 'network_stubs', '/I', '../vendor/ioLibrary_Driver/Internet/DHCP', 'dhcp_host_test.c')
    Invoke-Check 'network_name_host_test' @('network_name_host_test.c')
    Invoke-Check 'audio_packet_host_test' @('/I', '../usb', 'audio_packet_host_test.c')
    Invoke-Check 'ima_adpcm_host_test' @('/I', '../usb', 'ima_adpcm_host_test.c')
    Invoke-Check 'audio_tx_host_test' @('/I', '../usb', 'audio_tx_host_test.c')
    Invoke-Check 'udp_completion_host_test' @('udp_completion_host_test.c')
    Invoke-Check 'clock_observation_host_test' @('clock_observation_host_test.c')
    Invoke-Check 'clock_udp_rx_host_test' @('clock_udp_rx_host_test.c')
    Invoke-Check 'clock_spi_frame_host_test' @('clock_spi_frame_host_test.c')
    Invoke-Check 'clock_capture_service_host_test' @('clock_capture_service_host_test.c')
    Invoke-Check 'capture_reorder_host_test' @('capture_reorder_host_test.c')
    Invoke-Check 'audio_fec_host_test' @('/I', '../usb', 'audio_fec_host_test.c')
    Invoke-Check 'json_numeric_host_test' @('/utf-8', '/I', '../vendor/cJSON', 'json_numeric_host_test.c', '../vendor/cJSON/cJSON.c')
    $mbed = Join-Path $PicoSdk 'lib/mbedtls'
    Invoke-Check 'crypto_host_test' @('/DMBEDTLS_CONFIG_FILE="uab_mbedtls_config.h"', '/I', '..', '/I', "$mbed/include", '/I', "$mbed/library", 'crypto_host_test.c', '../uab_crypto.c', "$mbed/library/sha256.c", "$mbed/library/constant_time.c", "$mbed/library/platform_util.c")
} finally { Pop-Location }
