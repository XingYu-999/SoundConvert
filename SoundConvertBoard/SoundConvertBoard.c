#include <stdio.h>
#include "pico/stdlib.h"
#include "pico/multicore.h"
#include "pico/flash.h"
#include "usb_audio.h"
#include "uab_network.h"
#include "uab_watchdog.h"
#include "uab_boot.h"
#include "uab_indicator.h"
// Core 1 performs JSON and configuration transactions; the dedicated scratch
// stack bank is only 4 KiB, so put its 16 KiB stack in ordinary SRAM.
static uint32_t core1_stack[4096] __attribute__((aligned(8)));
int main(void) {
 stdio_init_all();
 puts("UAB starting: UART 115200 8N1; USB owner core0; W5500 owner core1");
 static uab_config_t boot_config;
 if (!uab_boot_initialize(&boot_config)) puts("UAB USB automatic start failed; management recovery remains available");
 uab_indicator_init(boot_config.indicator_mode);
 uab_network_prepare(&boot_config);
 if (!flash_safe_execute_core_init()) panic("Core 0 flash lockout initialization failed");
 uab_watchdog_init();
 uab_watchdog_status_t watchdog;
 uab_watchdog_get_status(&watchdog);
 if (watchdog.timeout_reboot) puts("UAB reset reason: owner watchdog timeout");
 multicore_launch_core1_with_stack(uab_network_main,core1_stack,sizeof(core1_stack));
 while (true) { usb_audio_task(); uab_indicator_task(); uab_watchdog_core0_poll(); tight_loop_contents(); }
}
