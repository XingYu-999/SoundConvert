#include <assert.h>
#include <stdio.h>
#include "../uab_clock_capture_service.h"
#include "../uab_udp_completion.h"
#include "../uab_clock_observation.c"
static unsigned checks,capture_calls,clock_calls,preflights,early_kind;
static bool enabled,pending,audio_pending,fec_pending;static unsigned queued;
static uint64_t now;static unsigned capture_work_us;
static uab_clock_observation_t observation;static uab_clock_anchor_t anchor;static uab_udp_completion_t tx;
#define CHECK(x) do{checks++;assert(x);}while(0)
static void capture_core(void){
 capture_calls++;now+=capture_work_us;
 if(early_kind==1)return; // empty/no-input
 if(early_kind==2)return; // inactive/invalid capture
 if(early_kind==3)return; // urgent audio return
 if(early_kind==4){enabled=false;return;}
 if(early_kind==5){pending=false;return;}
}
static void clock_count(void){clock_calls++;}
static void clock_try(void){
 clock_calls++;
 if(!uab_clock_observation_audio_budget(&observation,&anchor,now,audio_pending,fec_pending,queued,true))return;
 if(!uab_udp_clock_audio_grace_ready(&tx,(uint32_t)now))return;
 if(!uab_clock_observation_preflight_slot(&observation,&anchor,now,true,1))return;
 preflights++;
}
int main(void){
 enabled=false;pending=true;capture_work_us=0;uab_clock_capture_service(capture_core,&enabled,&pending,clock_count);CHECK(capture_calls==1&&clock_calls==0);
 enabled=true;pending=false;uab_clock_capture_service(capture_core,&enabled,&pending,clock_count);CHECK(capture_calls==2&&clock_calls==0);
 enabled=pending=true;
 for(early_kind=0;early_kind<=3;early_kind++){unsigned before=clock_calls;uab_clock_capture_service(capture_core,&enabled,&pending,clock_count);CHECK(clock_calls==before+1);}
 early_kind=4;unsigned before=clock_calls;uab_clock_capture_service(capture_core,&enabled,&pending,clock_count);CHECK(!enabled&&clock_calls==before);
 enabled=true;early_kind=5;uab_clock_capture_service(capture_core,&enabled,&pending,clock_count);CHECK(!pending&&clock_calls==before);
 // A representative natural gap: just after audio SEND, grace is not met.
 // Capture's empty return advances 80 us; its wrapper catches the valid phase
 // before the next full audio status/RX service would cross 500 us.
 uab_clock_identity_t id={1,4,2,3};CHECK(uab_clock_observation_enable(&observation,&id));observation.pending=true;observation.received_us=10000;
 anchor=(uab_clock_anchor_t){0,10000,3,48000,48,true};uab_udp_completion_submit_kind(&tx,10200,UAB_UDP_AUDIO);
 now=10220;clock_try();CHECK(preflights==0&&!observation.preflight_used);
 enabled=pending=true;early_kind=1;capture_work_us=80;uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(now==10300&&preflights==1&&observation.preflight_attempts==1);
 capture_work_us=20;uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(preflights==1&&observation.preflight_deferred==1);
 now=10510;clock_try();CHECK(preflights==1); // no expanded phase window
 // The wrapper cannot spend a slot or touch SPI when audio/FEC/queue work wins.
 anchor.sample_index+=48;anchor.extract_us=11000;now=11200;capture_work_us=0;
 audio_pending=true;early_kind=3;uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(preflights==1);
 audio_pending=false;fec_pending=true;uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(preflights==1);
 fec_pending=false;queued=1;uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(preflights==1);
 queued=0;
 uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(preflights==2&&observation.preflight_attempts==2);
 now=12001;uab_clock_capture_service(capture_core,&enabled,&pending,clock_try);CHECK(!observation.pending&&preflights==2&&observation.expired==1);
 printf("PASS: %u capture early-return/Legacy/fresh-state/natural-phase/audio-priority/once-anchor checks (host simulation only)\n",checks);return 0;
}
