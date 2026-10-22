# Firmware dependencies

Only the source files named in the board CMake target are compiled. Vendor
examples, protocol demonstrations and upstream tests are not part of the UAB
firmware or evidence of its correctness.

| Dependency | Pinned source | License | Compiled portion |
| --- | --- | --- | --- |
| WIZnet ioLibrary_Driver | `e1791974e76d0b00cbac9e349ed92477b8d41386` | MIT-style, `ioLibrary_Driver/license.txt` | Ethernet socket driver, chip configuration and W5500 SPI driver |
| cJSON | `6d9f2443ab071f86e5d9b43025a40929ec41c46c` | MIT, `cJSON/LICENSE` | `cJSON.c`, nesting limit 16, fixed 32 KiB arena |
| TinyUSB | `2f675abdd182163d03d4b25b47ad03497da23b1a` | MIT | Device stack and UAC1 implementation from the existing `D:/GithubClone/tinyusb` checkout |
| Raspberry Pi Pico SDK | 2.3.1 | BSD-3-Clause and component licenses | RP2350 HAL, dual core, hardware randomness, safe flash, UART and watchdog |
| Mbed TLS | `0bebf8b8c7f07abe3571ded48a11aa907a1ffb20` (SDK 2.3.1 submodule) | Apache-2.0 OR GPL-2.0-or-later, see SDK copy | SHA-256, Base64, constant-time helpers and zeroization only |

## Local TinyUSB diagnostic extension (V0.1.0)

The pinned TinyUSB checkout additionally applies `tinyusb-feedback-diagnostics.patch`.
It adds a read-only `tud_audio_n_get_feedback_state()` API for internal 16.16
feedback and FIFO byte-level diagnostics. The application snapshots it with USB
IRQ serialized. It does not change the USB wire format or feedback algorithm.
Apply this patch to a clean checkout at the pinned SHA before building V0.1.0;
do not replace the pinned dependency with an unreviewed newer branch.

## Local ioLibrary correction details

`ioLibrary-nonblocking-recv.patch` records the modification to `socket.c`.
The pinned IPv4 `recv()` checked nonblocking mode before checking available
bytes, so all nonblocking TCP reads returned `SOCK_BUSY` even with a complete
HELLO in RX memory. The patch checks `recvsize != 0` first, matching the IPv6
branch. This was diagnosed with a real TCP connection and subsequently verified
by an authenticated board session. Reapply or review the correction before
updating the dependency.

## DHCP implementation

The upstream DHCP implementation is **not compiled**. `uab_dhcp.c` uses the
ioLibrary DHCP header API but implements a separate bounded state machine on
hardware UDP socket 3. It validates received lengths/options, advances from a
monotonic clock, handles T1/T2/NAK/expiry, and reports every accepted ACK,
including same-address renewal. It submits SEND commands without waiting for
ARP or SENDOK. The network owner performs address conflict probing using socket
0 before making a changed address effective. No software IP stack is linked.

The deterministic DHCP test covers state and parsing decisions, not a real
DHCP server, Ethernet timing, cable changes, router compatibility or flash
power-failure behavior. Those need separate hardware evidence.

## Exact-number serialization on Pico

`cJSON-exact-numbers.patch` adds bounded integer-only decimal output for uint32
values and exactly representable Q8.8 fractions. The generic upstream path uses
`sprintf("%1.15g")` followed by a `sscanf("%lg")` round-trip; on the Pico owner
this can delay real-time audio while rendering otherwise small control/status
messages. Integer output preserves JSON number types and exact values, including
all legal USB master levels and `step_db=1/256`. Other floating-point values keep
the upstream path. `json_numeric_host_test.c` tests every legal UAC1 Q8.8 level
and uint32/boundary vectors against the production parser and serializer.

Network scheduling also services audio before background work and between the
two control slots. The 4 ms packet-age limit is unchanged. Per-stream phase
maxima are reset at STREAM_START so startup/DHCP maxima do not conceal steady
state stalls. Hardware before/after captures are needed to verify that this
fix eliminates the observed board-side expiration; source inspection alone is
not an end-to-end no-click guarantee.
