#include <stdatomic.h>
#include "pico/stdlib.h"
#include "hardware/watchdog.h"
#include "uab_watchdog.h"
static atomic_uint core1_heartbeat, core1_max_gap, feeds;
static uint32_t last_feed;
static bool timeout_reboot;
void uab_watchdog_init(void) {
  last_feed=time_us_32();
  atomic_store_explicit(&core1_heartbeat,last_feed,memory_order_release);
  timeout_reboot=watchdog_enable_caused_reboot();
  watchdog_enable(8000,true);
}
void uab_watchdog_core1_heartbeat(void) {
  uint32_t now=time_us_32();
  uint32_t previous=atomic_exchange_explicit(&core1_heartbeat,now,memory_order_acq_rel);
  uint32_t gap=now-previous;
  if (gap>atomic_load_explicit(&core1_max_gap,memory_order_relaxed))
    atomic_store_explicit(&core1_max_gap,gap,memory_order_relaxed);
}
void uab_watchdog_core0_poll(void) {
  uint32_t now=time_us_32();
  // If Core 0 hangs it cannot feed. If Core 1 hangs, Core 0 stops feeding after
  // this grace window. No timer/IRQ feeds on behalf of a stalled owner.
  if (now-last_feed>=100000 &&
      now-atomic_load_explicit(&core1_heartbeat,memory_order_acquire)<=2000000) {
    watchdog_update();
    last_feed=now;
    atomic_fetch_add_explicit(&feeds,1,memory_order_relaxed);
  }
}
void uab_watchdog_get_status(uab_watchdog_status_t *status) {
  status->core1_max_gap_us=atomic_load_explicit(&core1_max_gap,memory_order_relaxed);
  status->feed_count=atomic_load_explicit(&feeds,memory_order_relaxed);
  status->timeout_reboot=timeout_reboot;
}
