#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "uab_clock_observation.h"

// Socket 1 has one Core-1 owner. This optional path only consumes complete
// 104-byte datagrams. All other lengths leave RX_RD untouched for recvfrom;
// vendor sock_remained_size / packet flags are never read or modified here.
typedef struct {
 uint16_t (*size)(void *context);
 uint16_t (*read_pointer)(void *context);
 bool (*command_ready)(void *context);
 void (*read)(uint16_t pointer,uint8_t *data,uint16_t bytes,void *context);
 void (*commit)(uint16_t pointer,void *context);
 uint32_t (*now)(void *context);
 void *context;
} uab_clock_udp_rx_ops_t;
typedef struct {bool recv_pending;} uab_clock_udp_rx_t;
typedef struct {uint32_t header_us,read_us;bool header_read;} uab_clock_udp_rx_timing_t;
enum {UAB_CLOCK_RX_NONE=0,UAB_CLOCK_RX_FALLBACK=-1};

static int UAB_CLOCK_RAM(uab_clock_udp_rx)(uab_clock_udp_rx_t *s,const uab_clock_udp_rx_ops_t *ops,uint8_t data[UAB_CLOCK_PACKET_BYTES],uint8_t peer[4],uint16_t *port,uab_clock_udp_rx_timing_t *timing){
 void *ctx=ops->context;uint32_t started=ops->now(ctx);timing->header_us=timing->read_us=0;timing->header_read=false;
 if(s->recv_pending){if(!ops->command_ready(ctx))return UAB_CLOCK_RX_NONE;s->recv_pending=false;}
 uint16_t available=ops->size(ctx);
 if(available<8)return UAB_CLOCK_RX_NONE;
 // Two complete 16-bit SPI burst samples, at most one comparison. A changing
 // hardware size is deferred to the next service call, without spin or sleep.
 if(available!=ops->size(ctx)||!ops->command_ready(ctx))return UAB_CLOCK_RX_NONE;
 uint16_t pointer=ops->read_pointer(ctx);uint8_t header[8];
 uint32_t read_started=ops->now(ctx);timing->header_us=read_started-started;
 if(available>=8u+UAB_CLOCK_PACKET_BYTES){
  // Peek one contiguous hardware header+payload burst. Until length validation
  // succeeds this changes neither RX_RD nor vendor packet/remaining state.
  uint8_t packet[8u+UAB_CLOCK_PACKET_BYTES];ops->read(pointer,packet,sizeof(packet),ctx);timing->header_read=true;
  uint16_t bytes=(uint16_t)(((uint16_t)packet[6]<<8)|packet[7]);
  if(bytes!=UAB_CLOCK_PACKET_BYTES)return UAB_CLOCK_RX_FALLBACK;
  for(unsigned i=0;i<4;i++)peer[i]=packet[i];*port=(uint16_t)(((uint16_t)packet[4]<<8)|packet[5]);
  // Volatile RAM byte accesses deliberately prevent GCC recognizing this as a
  // call through the SDK's Flash memcpy veneer after the cold 1-2 Hz read.
  volatile uint8_t *destination=data;const volatile uint8_t *source=packet+8;
  for(unsigned i=0;i<UAB_CLOCK_PACKET_BYTES;i++)destination[i]=source[i];
  ops->commit((uint16_t)(pointer+8u+UAB_CLOCK_PACKET_BYTES),ctx);s->recv_pending=true;
  timing->read_us=ops->now(ctx)-read_started;return UAB_CLOCK_PACKET_BYTES;
 }
 ops->read(pointer,header,8,ctx);timing->header_read=true;
 uint16_t bytes=(uint16_t)(((uint16_t)header[6]<<8)|header[7]);
 if(bytes!=UAB_CLOCK_PACKET_BYTES)return UAB_CLOCK_RX_FALLBACK;
 return UAB_CLOCK_RX_NONE; // Valid fixed length, payload not fully available.
}
