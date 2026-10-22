// Production pure protocol/state machine. Simulated time does not establish
// real multicore, SPI timing, Windows render or physical audio acceptance.
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_clock_observation.c"
#include "../uab_udp_completion.h"
static unsigned checks;
#define CHECK(x) do { ++checks; assert(x); } while (0)
static const char request_hex[]="5541425401010068010203040506070811223344556677881020304050607080000000000000000100000000000003E80000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
static const char reply_hex[]="5541425401020068010203040506070811223344556677881020304050607080000000000000000100000000000003E800000000000007D000000000000007DA000000010000000200000000000007CF556677880000BB8000300001000000000000000000000000";
static uint8_t nibble(char c){return (uint8_t)(c>='A'?c-'A'+10:c-'0');}
static void decode(const char *s,uint8_t *p){for(unsigned i=0;i<104;i++)p[i]=(uint8_t)((nibble(s[2*i])<<4)|nibble(s[2*i+1]));}
static uab_clock_identity_t identity={UINT64_C(0x0102030405060708),UINT64_C(0x1020304050607080),0x11223344u,0x55667788u};
static uab_clock_observation_t fresh(void){uab_clock_observation_t s={0};CHECK(uab_clock_observation_enable(&s,&identity));return s;}
int main(void){
 uint8_t request[104],expected[104],reply[104];decode(request_hex,request);decode(reply_hex,expected);
 CHECK(strlen(request_hex)==208&&strlen(reply_hex)==208);
 uab_clock_observation_t s={0};CHECK(!uab_clock_observation_receive(&s,request,104,2000));CHECK(!s.pending&&s.submitted==0);
 s=fresh();CHECK(uab_clock_observation_receive(&s,request,104,2000));
 uab_clock_anchor_t anchor={UINT64_C(0x0000000100000002),1999,0x55667788u,48000,48,true};
 CHECK(uab_clock_observation_reply(&s,&anchor,2010,reply));CHECK(!memcmp(reply,expected,104));CHECK(s.pending&&s.requests==1);
 // Peer address/port is supplied by the socket owner; a packet cannot redirect
 // its reply. Owner loss rejects packets even when the token remains known.
 uab_clock_observation_t peer=fresh();CHECK(!uab_clock_observation_receive_from(&peer,request,104,2000,false,true));CHECK(!peer.pending&&peer.identity_rejected==1);
 CHECK(!uab_clock_observation_receive_from(&peer,request,104,2000,true,false));CHECK(!peer.pending&&peer.identity_rejected==2);
 CHECK(uab_clock_observation_receive_from(&peer,request,104,2000,true,true));
 // Every class of real audio work wins; only the first half of a known source
 // interval is eligible for time service. USB idle permits an invalid anchor.
 CHECK(!uab_clock_observation_audio_budget(&s,&anchor,2010,true,false,0,true));
 CHECK(!uab_clock_observation_audio_budget(&s,&anchor,2010,false,true,0,true));
 CHECK(!uab_clock_observation_audio_budget(&s,&anchor,2010,false,false,1,true));
 CHECK(!uab_clock_observation_audio_budget(&s,&anchor,2500,false,false,0,true));
 CHECK(uab_clock_observation_audio_budget(&s,&anchor,2499,false,false,0,true));
 CHECK(!uab_clock_observation_audio_budget(&s,NULL,2499,false,false,0,true));
 CHECK(uab_clock_observation_audio_budget(&s,NULL,2499,false,false,0,false));
 // Busy does not consume or resend an old B3. Final retry rebuilds it.
 CHECK(uab_clock_observation_reply(&s,&anchor,2020,reply));CHECK(r64(reply+56)==2020);CHECK(s.pending);
 CHECK(uab_clock_observation_enable(&s,&identity));CHECK(s.last_request_id==1&&s.next_allowed_us==252000);
 CHECK(!uab_clock_observation_receive(&s,request,104,3000)&&s.replay_rejected==1);
 w64(request+32,2);CHECK(!uab_clock_observation_receive(&s,request,104,3000)&&s.limited==1);
 CHECK(!uab_clock_observation_expire(&s,4000));CHECK(uab_clock_observation_expire(&s,4001));CHECK(!s.pending&&s.expired==1);
 w64(request+32,3);CHECK(uab_clock_observation_receive(&s,request,104,252000));
 anchor.valid=false;CHECK(uab_clock_observation_reply(&s,&anchor,252001,reply));for(unsigned i=64;i<104;i++)CHECK(reply[i]==0);
 anchor.valid=true;anchor.generation++;CHECK(uab_clock_observation_reply(&s,&anchor,252001,reply)&&r16(reply+90)==0);anchor.generation--;
 anchor.extract_us=252002;CHECK(uab_clock_observation_reply(&s,&anchor,252001,reply)&&r16(reply+90)==0);
 anchor.extract_us=247999;CHECK(uab_clock_observation_reply(&s,&anchor,252001,reply)&&r16(reply+90)==0);
 anchor.extract_us=248001;CHECK(uab_clock_observation_reply(&s,&anchor,252001,reply)&&r16(reply+90)==1);
 // Every response-only/reserved byte in a request must be zero.
 for(unsigned i=48;i<104;i++){uab_clock_observation_t t=fresh();decode(request_hex,request);request[i]=1;CHECK(!uab_clock_observation_receive(&t,request,104,2000));CHECK(t.invalid==1&&!t.pending);}
 const unsigned offsets[]={0,3,4,5,6,7,8,15,16,19,20,23,24,31};
 for(unsigned k=0;k<sizeof(offsets)/sizeof(offsets[0]);k++){uab_clock_observation_t t=fresh();decode(request_hex,request);request[offsets[k]]^=0x80;CHECK(!uab_clock_observation_receive(&t,request,104,2000));CHECK(!t.pending);}
 decode(request_hex,request);s=fresh();CHECK(!uab_clock_observation_receive(&s,request,103,2000));CHECK(!uab_clock_observation_receive(&s,request,105,2000));
 w64(request+32,0);CHECK(!uab_clock_observation_receive(&s,request,104,2000));
 // One bucket token per 250 ms; no catch-up/burst even after a long gap.
 s=fresh();decode(request_hex,request);CHECK(uab_clock_observation_receive(&s,request,104,1000000));s.pending=false;
 for(unsigned i=2;i<=100;i++){w64(request+32,i);CHECK(!uab_clock_observation_receive(&s,request,104,1000000+i));}
 w64(request+32,101);CHECK(uab_clock_observation_receive(&s,request,104,1250000));CHECK(s.requests==2&&s.limited==99);
 // 32-bit microsecond wrap does not affect the uint64 wire exchange or anchor.
 s=fresh();decode(request_hex,request);uint64_t edge=UINT64_C(0x1fffffff00);CHECK(uab_clock_observation_receive(&s,request,104,edge));anchor.extract_us=edge-1;
 CHECK(uab_clock_observation_reply(&s,&anchor,edge+300,reply));CHECK(r64(reply+48)==edge&&r64(reply+56)==edge+300&&r64(reply+72)==edge-1);
 // Invalid rollback timestamps expire, never underflow into a valid reply.
 CHECK(!uab_clock_observation_reply(&s,&anchor,edge-1,reply));CHECK(!s.pending);
 // Full unsigned fields survive without float/signed conversions.
 uab_clock_identity_t maximum={UINT64_MAX,UINT64_MAX,UINT32_MAX,UINT32_MAX};s=(uab_clock_observation_t){0};CHECK(uab_clock_observation_enable(&s,&maximum));
 decode(request_hex,request);memset(request+8,255,40);CHECK(uab_clock_observation_receive(&s,request,104,9));
 CHECK(uab_clock_observation_reply(&s,NULL,10,reply));CHECK(r64(reply+8)==UINT64_MAX&&r64(reply+24)==UINT64_MAX&&r64(reply+32)==UINT64_MAX&&r64(reply+40)==UINT64_MAX);
 uab_clock_observation_disable(&s);CHECK(!s.enabled&&!s.pending&&s.last_request_id==0&&s.revoked==1);
 CHECK(!uab_clock_observation_receive(&s,request,104,250009));CHECK(s.submitted==0);
 s=fresh();CHECK(uab_clock_observation_matches(&s,&identity));maximum=identity;maximum.epoch++;CHECK(!uab_clock_observation_matches(&s,&maximum));
 // Kind remains owned until actual SENDOK. Clock expires after 2 ms, whereas
 // PCM/FEC/probe keep their existing 1 s watchdog. Wrap-safe subtraction.
 uab_udp_completion_t tx={0};uab_udp_completion_submit_kind(&tx,0xffffff00u,UAB_UDP_CLOCK);CHECK(!uab_udp_completion_ready(&tx));
 CHECK(!uab_udp_completion_stalled(&tx,0x000006d0u));CHECK(uab_udp_completion_stalled(&tx,0x000006d1u));CHECK(tx.kind==UAB_UDP_CLOCK);
 CHECK(uab_udp_completion_ack(&tx));CHECK(!uab_udp_completion_ack(&tx));CHECK(tx.kind==UAB_UDP_OTHER&&uab_udp_completion_ready(&tx));
 for(uab_udp_kind_t kind=UAB_UDP_OTHER;kind<UAB_UDP_CLOCK;kind++){uab_udp_completion_submit_kind(&tx,100,kind);CHECK(!uab_udp_completion_stalled(&tx,2101));CHECK(uab_udp_completion_stalled(&tx,1000101));uab_udp_completion_reset(&tx);}
 uab_udp_completion_submit_kind(&tx,100,UAB_UDP_CLOCK);uab_udp_event_t event=uab_udp_completion_poll(&tx,false,false,2100);CHECK(event.type==UAB_UDP_EVENT_NONE&&tx.pending);
 event=uab_udp_completion_poll(&tx,false,false,2101);CHECK(event.type==UAB_UDP_EVENT_TIMEOUT&&event.kind==UAB_UDP_CLOCK&&event.software_timeout&&!tx.pending);
 uab_udp_completion_submit_kind(&tx,100,UAB_UDP_CLOCK);event=uab_udp_completion_poll(&tx,true,false,101);CHECK(event.type==UAB_UDP_EVENT_COMPLETED&&event.kind==UAB_UDP_CLOCK&&!tx.pending);
 event=uab_udp_completion_poll(&tx,true,false,102);CHECK(event.type==UAB_UDP_EVENT_NONE); // duplicate SENDOK
 uab_udp_completion_submit_kind(&tx,100,UAB_UDP_CLOCK);event=uab_udp_completion_poll(&tx,true,true,101);CHECK(event.type==UAB_UDP_EVENT_TIMEOUT&&event.kind==UAB_UDP_CLOCK&&!event.software_timeout&&!tx.pending);
 for(uab_udp_kind_t kind=UAB_UDP_AUDIO;kind<=UAB_UDP_PROBE;kind++){uab_udp_completion_submit_kind(&tx,100,kind);event=uab_udp_completion_poll(&tx,false,false,2101);CHECK(event.type==UAB_UDP_EVENT_NONE&&tx.pending);event=uab_udp_completion_poll(&tx,true,false,2102);CHECK(event.type==UAB_UDP_EVENT_COMPLETED&&event.kind==kind&&!tx.pending);}
 s=fresh();s.requests=8;s.expired=2;uab_clock_observation_reset_stats(&s);CHECK(s.enabled&&s.requests==0&&s.expired==0&&uab_clock_observation_matches(&s,&identity));
 // The 100 us service budget is a safety stop, not just telemetry. Latching
 // survives both normal disable and counter reset for the offending identity.
 CHECK(uab_clock_observation_record_work(&s,100));CHECK(s.enabled&&!s.budget_latched&&s.processing_max_us==100&&s.service_budget_breaches==0);
 decode(request_hex,request);CHECK(uab_clock_observation_receive(&s,request,104,2000));
 CHECK(!uab_clock_observation_record_work_phase(&s,101,UAB_CLOCK_BUDGET_RX));CHECK(!s.enabled&&!s.pending&&s.budget_latched&&s.budget_latched_us==101&&s.service_budget_breaches==1&&s.budget_latched_phase==UAB_CLOCK_BUDGET_RX);
 CHECK(!uab_clock_observation_enable(&s,&identity));CHECK(!s.enabled&&s.budget_latched);
 uab_clock_observation_disable(&s);CHECK(!uab_clock_observation_enable(&s,&identity));
 uab_clock_observation_reset_stats(&s);CHECK(s.service_budget_breaches==0&&s.processing_max_us==0&&s.budget_latched&&s.budget_latched_us==101&&s.budget_latched_phase==UAB_CLOCK_BUDGET_RX);
 CHECK(!uab_clock_observation_enable(&s,&identity));CHECK(!uab_clock_observation_reply(&s,&anchor,2010,reply));
 CHECK(!uab_clock_observation_record_work(&s,0));CHECK(s.budget_latched); // a cheap later call cannot forgive the breach
 uab_clock_identity_t next_identity=identity;next_identity.epoch++;
 CHECK(uab_clock_observation_enable(&s,&next_identity));CHECK(s.enabled&&!s.budget_latched&&s.budget_latched_us==0&&uab_clock_observation_matches(&s,&next_identity));
 CHECK(!uab_clock_observation_record_work(&s,UINT32_MAX));CHECK(s.budget_latched&&s.budget_latched_us==UINT32_MAX&&!s.enabled);
 next_identity.stream_id++;next_identity.token++;
 CHECK(uab_clock_observation_enable(&s,&next_identity));CHECK(!s.budget_latched&&s.enabled);
 // Only an exact destination actually written by this socket owner is cached.
 // Lifecycle invalidation defeats old-address/old-port reuse after reopen or
 // a new stream, even when its tuple happens to be the same as before.
 uab_udp_destination_t destination={0};uint8_t peer_a[4]={192,168,8,74},peer_b[4]={192,168,8,75};
 CHECK(!uab_udp_destination_matches(&destination,peer_a,49162));uab_udp_destination_record(&destination,peer_a,49162);
 CHECK(uab_udp_destination_matches(&destination,peer_a,49162));CHECK(!uab_udp_destination_matches(&destination,peer_a,49164));
 for(unsigned byte=0;byte<4;byte++){peer_b[byte]=peer_a[byte]^1;CHECK(!uab_udp_destination_matches(&destination,peer_b,49162));peer_b[byte]=peer_a[byte];}
 uab_udp_destination_reset(&destination);CHECK(!uab_udp_destination_matches(&destination,peer_a,49162));
 peer_b[3]=75;uab_udp_destination_record(&destination,peer_b,49164);CHECK(!uab_udp_destination_matches(&destination,peer_a,49162));CHECK(uab_udp_destination_matches(&destination,peer_b,49164));
 uab_udp_destination_record(&destination,peer_a,0);CHECK(!uab_udp_destination_matches(&destination,peer_a,0));
 for(uab_clock_budget_phase_t phase=UAB_CLOCK_BUDGET_RX;phase<=UAB_CLOCK_BUDGET_TX_COMPLETE;phase++){
  s=fresh();CHECK(uab_clock_observation_record_work_phase(&s,100,phase));CHECK(!uab_clock_observation_record_work_phase(&s,101,phase));CHECK(s.budget_latched_phase==phase);
  CHECK(!uab_clock_observation_record_work_phase(&s,200,UAB_CLOCK_BUDGET_UNKNOWN));CHECK(s.budget_latched_phase==phase&&s.budget_latched_us==101);
  uab_clock_observation_reset_stats(&s);CHECK(s.budget_latched_phase==phase&&s.budget_latched&&s.budget_latched_us==101);
 }
 // A main-loop tail fixed at extraction+750 us never satisfies the original
 // 500 us phase budget. Natural audio service at +250 us does, with exactly
 // one hardware preflight permission per anchor, including JSON-yield calls.
 s=fresh();decode(request_hex,request);CHECK(uab_clock_observation_receive(&s,request,104,10000));
 anchor=(uab_clock_anchor_t){0,10000,identity.epoch,48000,48,true};
 CHECK(!uab_clock_observation_audio_budget(&s,&anchor,10750,false,false,0,true));
 CHECK(uab_clock_observation_audio_budget(&s,&anchor,10250,false,false,0,true));
 CHECK(!uab_clock_observation_preflight_slot(&s,&anchor,10199,true,1));CHECK(s.preflight_attempts==0&&s.preflight_phase_waits==1);
 CHECK(uab_clock_observation_preflight_slot(&s,&anchor,10200,true,1));CHECK(s.preflight_attempts==1);
 for(unsigned call=0;call<10;call++)CHECK(!uab_clock_observation_preflight_slot(&s,&anchor,10250+call,true,1+call));
 CHECK(s.preflight_attempts==1&&s.preflight_deferred==10);
 // A busy attempt cannot retry that anchor; only the next real source block
 // can grant another permission, without extending the request's 2 ms expiry.
 anchor.sample_index+=48;anchor.extract_us+=1000;CHECK(uab_clock_observation_preflight_slot(&s,&anchor,11250,true,20));CHECK(s.preflight_attempts==2);
 CHECK(!uab_clock_observation_preflight_slot(&s,&anchor,11501,true,21));
 CHECK(!uab_clock_observation_preflight_slot(&s,&anchor,12001,true,22));CHECK(!s.pending&&s.expired==1);
 s=fresh();CHECK(uab_clock_observation_receive(&s,request,104,20000));anchor.extract_us=19500;
 CHECK(uab_clock_observation_preflight_slot(&s,&anchor,20000,true,30)); // exactly 500 us
 CHECK(!uab_clock_observation_preflight_slot(&s,&anchor,20001,true,31)); // never widen to the 4 ms anchor-valid limit
 s=fresh();CHECK(uab_clock_observation_receive(&s,request,104,30000));
 CHECK(uab_clock_observation_preflight_slot(&s,NULL,30001,false,40));CHECK(!uab_clock_observation_preflight_slot(&s,NULL,30002,false,40));CHECK(uab_clock_observation_preflight_slot(&s,NULL,30003,false,41));
 // Extraction age never substitutes for actual SEND age. Ordinary pending
 // kinds defer for 39 us without consuming a slot or doing a hardware poll.
 tx=(uab_udp_completion_t){0};CHECK(uab_udp_clock_audio_grace_ready(&tx,0));
 for(uab_udp_kind_t kind=UAB_UDP_OTHER;kind<UAB_UDP_CLOCK;kind++){
  uab_udp_completion_submit_kind(&tx,1000,kind);CHECK(!uab_udp_clock_audio_grace_ready(&tx,1000));CHECK(!uab_udp_clock_audio_grace_ready(&tx,1039));CHECK(uab_udp_clock_audio_grace_ready(&tx,1040));CHECK(uab_udp_clock_audio_grace_ready(&tx,1041));
  uab_udp_completion_submit_kind(&tx,0xfffffff0u,kind);CHECK(!uab_udp_clock_audio_grace_ready(&tx,0x00000017u));CHECK(uab_udp_clock_audio_grace_ready(&tx,0x00000018u));
 }
 uab_udp_completion_submit_kind(&tx,1000,UAB_UDP_CLOCK);CHECK(uab_udp_clock_audio_grace_ready(&tx,1000));
 uab_udp_completion_reset(&tx);CHECK(uab_udp_clock_audio_grace_ready(&tx,0));
 s=fresh();CHECK(uab_clock_observation_receive(&s,request,104,40000));anchor=(uab_clock_anchor_t){0,39700,identity.epoch,48000,48,true};
 uab_udp_completion_submit_kind(&tx,39980,UAB_UDP_AUDIO);CHECK(uab_clock_observation_audio_budget(&s,&anchor,40000,false,false,0,true));
 CHECK(!uab_udp_clock_audio_grace_ready(&tx,40000));CHECK(!s.preflight_used&&s.preflight_attempts==0);
 CHECK(uab_udp_clock_audio_grace_ready(&tx,40020));CHECK(uab_clock_observation_preflight_slot(&s,&anchor,40020,true,50));CHECK(!uab_clock_observation_preflight_slot(&s,&anchor,40030,true,51));
 printf("PASS: clock observation %u checks, 104-byte golden vectors, identities/reserved/replay/rate/deadline/anchor/uint64 and completion kinds (host simulation only)\n",checks);return 0;
}
