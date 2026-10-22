# USB focused checks

V0.12.2 adds `indicator_host_test.c`, exercising the production LD2 PWM controller
with fake USB snapshots, time and PWM registers. It covers connection/activity
transitions, microphone-only traffic, no-data timeout, five modes, temporary
identify override and timer wrap. Configuration tests also cover v1-v7 migration
to v8 and mode persistence/invalid records. These are offline checks, not board tests.

Run all portable checks with the installed Visual Studio C++ build tools:

```powershell
./Run-HostChecks.ps1
```

The script builds into `../build/host-checks` and uses the SDK 2.3.1 Mbed TLS
source. It does not flash, reset or connect to the board.

`boot_host_test.c` links the production `uab_boot.c` and USB owner with simulated
Flash loading and TinyUSB calls; no network module is linked. Five fresh-process
scenarios verify saved Unicode names and default names before the first attach,
one automatic START per boot, STOP remaining stopped, explicit restart, and no
automatic retry after invalid names or USB initialization failure and persisted high-resolution profile before attach (89 checks).
The real entry point consumes this START before launching Core 1. An explicitly
detected W5500 initialization fault keeps Core 1 heartbeats and drains its bounded
USB queue, preserving independent USB service; a genuinely stalled owner still
times out through the watchdog. Physical no-controller enumeration, no-link
behavior and actual hardware-fault timing require separate board evidence.

`usb_audio_host_test.c` includes the production USB owner and replaces only the
Pico/TinyUSB platform functions with deterministic stubs. It exercises fixed-rate
request validation, master-only channel/entity bounds, volume limits, -20 dB
amplitude of 0.1 applied once to both channels, exact mute and unity restoration, FIFO
overflow invalidation, stale queue rejection, suspend/resume, delayed STOP, and
restart without reinitializing the USB stack. It does not test IRQ safety,
cross-core ordering, USB host compatibility, feedback timing or electrical power.

On Windows with Visual Studio 2022 C++ tools and the Windows SDK installed, run
`./Run-HostChecks.ps1`. It discovers the compiler and builds the portable test
binaries in the output directory described above.

Alternatively, from a Visual Studio developer terminal in this directory:

```bat
cl /nologo /std:c11 /experimental:c11atomics /I stubs /I ..\usb usb_audio_host_test.c /Fe:usb_audio_host_test.exe
usb_audio_host_test.exe
cl /nologo /std:c11 /I stubs /I ..\usb config_store_host_test.c /Fe:config_store_host_test.exe
config_store_host_test.exe
cl /nologo /std:c11 /experimental:c11atomics /I stubs watchdog_host_test.c /Fe:watchdog_host_test.exe
watchdog_host_test.exe
```

After building the ARM firmware, inspect the actual linked descriptor bytes:

```powershell
python .\check_usb_descriptors.py ..\build\SoundConvertBoard.elf
```

The descriptor check verifies total length, AC/AS alternatives, endpoint sizes,
48 kHz stereo S16, explicit three-byte feedback, master-only mute/volume bitmaps
with zero L/R controls, descriptor revision 0x0101 and absence of CDC/HID interfaces.

`config_store_host_test.c` runs the production slot encoder/decoder and commit
logic against simulated NOR memory. It checks the standard CRC32 vector,
idempotent writes, interrupted erase/data/commit operations, corrupted-newest-slot
fallback and refusal to persist while USB runs. Real flash endurance, power cuts,
dual-core lockout and preservation across firmware updates need board tests.

`watchdog_host_test.c` verifies the production watchdog feed gate, stale owner,
recovery, timeout reboot reporting and microsecond-counter wrap. Actual reset
timing and debugger pause behavior require hardware.

`dhcp_host_test.c` includes the production bounded DHCP client with simulated
hardware UDP and monotonic time. Its 17 checks cover DORA, T1 unicast renewal,
same-address ACK notification, T2 broadcast rebinding, NAK invalidation, expiry,
malformed option length and infinite leases. It does not cover real server
interoperability, ARP conflict probing or link recovery.

```bat
cl /nologo /std:c11 /I network_stubs /I ..\vendor\ioLibrary_Driver\Internet\DHCP dhcp_host_test.c /Fe:..\build\dhcp_host_test.exe /Fo:..\build\dhcp_host_test.obj
..\build\dhcp_host_test.exe
```

If the installed Visual Studio environment script fails on a third-party PATH
entry containing parentheses, invoke it from a process with a minimal Windows
PATH, or explicitly configure INCLUDE/LIB for the installed MSVC and Windows SDK.

`crypto_host_test.c` executes the production SHA-256 and byte comparison helpers used for stable device identity and duplicate-request digests. Pairing HMAC/HKDF/Base64 helpers were removed in 0.4.0-dev.

The current USB suite has 95 checks including packed 24-bit sign and amplitude, 96-frame blocks, alternate/rate transitions, FIFO feedback thresholds, fractional Q8.8 Host writes, one-block mute ramp and short-block timeline invalidation. Config-store tests include a separately encoded v1 fixture: legacy names/network/revision survive migration, high-resolution defaults off, v2 persists it, and corruption falls back to the older slot. audio_packet_host_test.c executes the production UABA encoder for 240/624-byte frames, big-endian metadata, exact PCM bytes and invalid capacity/format bounds (14 checks).

`audio_tx_host_test.c` verifies the production single-pending-packet sender:
transient hardware busy preserves bytes/sequence; successful submission happens
once; the original 4 ms age limit remains; stale generations, stream ownership,
explicit cancellation and microsecond wrap are handled. `json_numeric_host_test.c`
round-trips uint32 and every legal Q8.8 value through the patched production cJSON
serializer/parser without a general floating formatter for those exact values.
