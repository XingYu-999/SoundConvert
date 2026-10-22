#ifndef UAB_UDP_PORTS_H
#define UAB_UDP_PORTS_H
#include <stdint.h>
#include <stdbool.h>

typedef bool (*uab_udp_port_busy_fn)(uint16_t port, void *context);
/* Explicit zero selects an ephemeral port. Missing fields are mapped to the
 * legacy port by the control parser before this bounded allocator is called. */
static inline uint16_t uab_udp_port_select(int requested, uint32_t seed, uab_udp_port_busy_fn busy, void *context) {
 if(requested && (requested<1024 || requested>65535))return 0;
 for(unsigned attempt=0;attempt<(requested?1u:32u);attempt++) {
  uint16_t port=requested?(uint16_t)requested:(uint16_t)(49152u+((seed+attempt)&16383u));
  if(port==49160u || port==49161u || busy(port,context))continue;
  return port;
 }
 return 0;
}
#endif
