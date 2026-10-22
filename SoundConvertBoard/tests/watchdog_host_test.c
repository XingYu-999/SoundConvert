#include <assert.h>
#include <stdio.h>
#include "../uab_watchdog.c"
static uint32_t fake_time=100, actual_feeds;
uint32_t time_us_32(void) { return fake_time; }
bool watchdog_enable_caused_reboot(void) { return true; }
void watchdog_enable(uint32_t milliseconds, bool pause_on_debug) {
  assert(milliseconds==8000 && pause_on_debug);
}
void watchdog_update(void) { ++actual_feeds; }
int main(void) {
  uab_watchdog_init();
  uab_watchdog_core0_poll(); assert(!actual_feeds);
  fake_time+=100000; uab_watchdog_core0_poll(); assert(actual_feeds==1);
  fake_time+=2100000; uab_watchdog_core0_poll(); assert(actual_feeds==1);
  uab_watchdog_core1_heartbeat(); uab_watchdog_core0_poll(); assert(actual_feeds==2);
  uab_watchdog_status_t status; uab_watchdog_get_status(&status);
  assert(status.timeout_reboot && status.core1_max_gap_us==2200000 && status.feed_count==2);
  fake_time=0xffff0000u; uab_watchdog_core1_heartbeat(); uab_watchdog_core0_poll();
  uint32_t before=actual_feeds;
  fake_time+=200000; uab_watchdog_core1_heartbeat(); uab_watchdog_core0_poll();
  assert(actual_feeds==before+1);
  puts("PASS: watchdog feed gate, stale owner, recovery, boot reason and timer wrap");
  return 0;
}
