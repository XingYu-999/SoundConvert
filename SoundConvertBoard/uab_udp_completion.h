#pragma once
#include <stdbool.h>
#include <stdint.h>
typedef enum { UAB_UDP_OTHER, UAB_UDP_AUDIO, UAB_UDP_FEC, UAB_UDP_PROBE, UAB_UDP_CLOCK } uab_udp_kind_t;
// A socket-owner cache of the destination actually written to the chip. Stream
// and socket lifecycle changes invalidate it; packet-provided addresses do not
// authorize a new target. Clock may skip a write only on an exact IP+port match.
typedef struct {bool valid;uint8_t ip[4];uint16_t port;} uab_udp_destination_t;
static inline void uab_udp_destination_reset(uab_udp_destination_t*d){d->valid=false;}
static inline void uab_udp_destination_record(uab_udp_destination_t*d,const uint8_t ip[4],uint16_t port){for(unsigned i=0;i<4;i++)d->ip[i]=ip[i];d->port=port;d->valid=port!=0;}
static inline bool uab_udp_destination_matches(const uab_udp_destination_t*d,const uint8_t ip[4],uint16_t port){return d->valid&&d->port==port&&d->ip[0]==ip[0]&&d->ip[1]==ip[1]&&d->ip[2]==ip[2]&&d->ip[3]==ip[3];}
typedef struct{bool pending;uint32_t submitted_us;uab_udp_kind_t kind;}uab_udp_completion_t;
#define UAB_UDP_CLOCK_AUDIO_GRACE_US 40u
// A software extraction anchor can be old when PCM is only just submitted.
// Defer a clock's one preflight opportunity until that actual SEND has had a
// candidate grace period. It is a scheduling heuristic, not a safety budget
// or SENDOK guarantee. Actual CR/SR/SENDOK/FSR/RD/WR checks remain mandatory.
// This reads memory only and never waits.
static inline bool uab_udp_clock_audio_grace_ready(const uab_udp_completion_t*s,uint32_t now){return !s->pending||s->kind==UAB_UDP_CLOCK||(uint32_t)(now-s->submitted_us)>=UAB_UDP_CLOCK_AUDIO_GRACE_US;}
static inline void uab_udp_completion_reset(uab_udp_completion_t*s){s->pending=false;s->kind=UAB_UDP_OTHER;}
static inline void uab_udp_completion_submit_kind(uab_udp_completion_t*s,uint32_t now,uab_udp_kind_t kind){s->pending=true;s->submitted_us=now;s->kind=kind;}
static inline void uab_udp_completion_submit(uab_udp_completion_t*s,uint32_t now){uab_udp_completion_submit_kind(s,now,UAB_UDP_OTHER);}
static inline bool uab_udp_completion_ack(uab_udp_completion_t*s){bool was=s->pending;uab_udp_completion_reset(s);return was;}
static inline bool uab_udp_completion_ready(const uab_udp_completion_t*s){return !s->pending;}
// A time-sideband send may never reserve the audio socket for the legacy 1 s
// watchdog. Expiration is handled by the socket owner, without ending audio.
static inline bool uab_udp_completion_stalled(const uab_udp_completion_t*s,uint32_t now){return s->pending&&(uint32_t)(now-s->submitted_us)>(s->kind==UAB_UDP_CLOCK?2000u:1000000u);}
typedef enum { UAB_UDP_EVENT_NONE, UAB_UDP_EVENT_COMPLETED, UAB_UDP_EVENT_TIMEOUT } uab_udp_event_type_t;
typedef struct { uab_udp_event_type_t type; uab_udp_kind_t kind; bool software_timeout; } uab_udp_event_t;
// Preserve the kind before consuming completion ownership. A contradictory
// TIMEOUT+SENDOK is a failure of that same datagram, never a new audio failure.
static inline uab_udp_event_t uab_udp_completion_poll(uab_udp_completion_t*s,bool sendok,bool timeout,uint32_t now){
 uab_udp_event_t event={UAB_UDP_EVENT_NONE,s->kind,false};
 if(timeout){event.type=UAB_UDP_EVENT_TIMEOUT;uab_udp_completion_reset(s);}
 else if(sendok){if(uab_udp_completion_ack(s))event.type=UAB_UDP_EVENT_COMPLETED;}
 else if(uab_udp_completion_stalled(s,now)){event.type=UAB_UDP_EVENT_TIMEOUT;event.software_timeout=true;uab_udp_completion_reset(s);}
 return event;
}
