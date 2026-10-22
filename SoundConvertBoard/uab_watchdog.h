#ifndef UAB_WATCHDOG_H
#define UAB_WATCHDOG_H
#include <stdbool.h>
#include <stdint.h>
typedef struct {
  uint32_t core1_max_gap_us;
  uint32_t feed_count;
  bool timeout_reboot;
} uab_watchdog_status_t;
void uab_watchdog_init(void); // Core 0, before launching Core 1
void uab_watchdog_core0_poll(void); // after USB owner makes progress
void uab_watchdog_core1_heartbeat(void); // once per completed network poll
void uab_watchdog_get_status(uab_watchdog_status_t *status);
#endif
