#include <assert.h>
#include <stdio.h>
#include "../uab_udp_completion.h"
int main(void){
  uab_udp_completion_t state={0};assert(uab_udp_completion_ready(&state));
  uab_udp_completion_submit(&state,0xfffffff0u);
  // Even if a chip's command register clears and its memory read pointer advances, no IRQ means no new datagram.
  assert(!uab_udp_completion_ready(&state));assert(!uab_udp_completion_stalled(&state,20));
  assert(uab_udp_completion_ack(&state));assert(uab_udp_completion_ready(&state));assert(!uab_udp_completion_ack(&state));
  uab_udp_completion_submit(&state,50);assert(uab_udp_completion_stalled(&state,1000051));
  uab_udp_completion_reset(&state);assert(uab_udp_completion_ready(&state));
  puts("PASS: UDP send completion ownership, duplicate completion, watchdog and timer wrap");return 0;
}
