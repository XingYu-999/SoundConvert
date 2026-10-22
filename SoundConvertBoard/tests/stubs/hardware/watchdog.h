#include <stdbool.h>
#include <stdint.h>
bool watchdog_enable_caused_reboot(void);
void watchdog_enable(uint32_t milliseconds, bool pause_on_debug);
void watchdog_update(void);
