#include "usb_microphone.h"
#include "pico/stdlib.h"
#include "pico/critical_section.h"
#include "tusb.h"
#include <stdatomic.h>
#include <string.h>
#include <math.h>
#include <limits.h>
#include "usb_control_notify.h"
#define MIC_PACKETS 64u
typedef struct {uint32_t generation;uint16_t bytes,gap_frames;uint8_t pcm[USB_MIC_MAX_BYTES];}mic_packet_t;
static mic_packet_t packets[MIC_PACKETS],pending;static unsigned pending_at,pending_gap;
static atomic_uint wr,rd,queued_bytes,queue_drops,stale_packets,received_frames,accept_generation,accept_rate,accept_bits,tx_bytes,tx_packets;
static atomic_bool accepting;
static atomic_bool network_enabled;static atomic_uint control_mailbox,network_reset_serial,network_discard_before;
// Core1 owns the timing writer. Core0 never waits for it: a raced mailbox
// observation is explicitly invalid, rather than a fabricated enqueue age.
static atomic_uint enqueue_version,enqueue_at_us,enqueue_maximum_gap_us;static atomic_bool enqueue_seen;
static uint32_t last_task_us;static bool task_seen;
static unsigned observed_network_reset,discard_before;
static float gain_current=1,gain_target=1,gain_step;static unsigned gain_remaining;static uint8_t gain_scratch[USB_MIC_MAX_BYTES];
// Keep the last committed frame, not the last generated frame. A short FIFO
// write may even stop inside a packed 24-bit sample.
typedef struct{float gain;unsigned gain_remaining;int32_t last,anchor;unsigned remaining;bool gap;}mic_dsp_state_t;
static mic_dsp_state_t staged_states[USB_MIC_MAX_BYTES/2];
static unsigned staged_bytes,staged_at,staged_committed,staged_stride;static uint32_t staged_revision;
static bool staged_gap,staged_fallback,transition_gap;static int32_t transition_last,transition_anchor;static unsigned transition_remaining;
void usb_microphone_set_network_enabled(bool enabled){atomic_store(&network_enabled,enabled);if(!enabled){atomic_fetch_add_explicit(&enqueue_version,1,memory_order_acq_rel);atomic_store_explicit(&enqueue_seen,false,memory_order_relaxed);atomic_fetch_add_explicit(&enqueue_version,1,memory_order_release);atomic_store(&network_discard_before,atomic_load_explicit(&wr,memory_order_acquire));atomic_fetch_add_explicit(&network_reset_serial,1,memory_order_release);}}
static usb_mic_status_t state,snapshot;static critical_section_t lock;
static uint32_t silence_packet,silence_remainder;static uint16_t flow_threshold;
static bool ready,pending_valid;
static void publish(void){state.queue_drops=atomic_load(&queue_drops);state.stale_packets=atomic_load(&stale_packets);state.received_frames=atomic_load(&received_frames);state.queued_packets=atomic_load(&wr)-atomic_load(&rd);state.tx_frames=atomic_load(&tx_bytes)/(state.bits/8);state.tx_packets=atomic_load(&tx_packets);state.maximum_enqueue_gap_us=atomic_load_explicit(&enqueue_maximum_gap_us,memory_order_relaxed);critical_section_enter_blocking(&lock);snapshot=state;critical_section_exit(&lock);}
static bool enqueue_age(uint32_t now,uint32_t *age){
 for(unsigned retry=0;retry<3;retry++){unsigned before=atomic_load_explicit(&enqueue_version,memory_order_acquire);if(before&1u)continue;bool seen=atomic_load_explicit(&enqueue_seen,memory_order_relaxed);uint32_t at=atomic_load_explicit(&enqueue_at_us,memory_order_relaxed);atomic_thread_fence(memory_order_acq_rel);if(before==atomic_load_explicit(&enqueue_version,memory_order_acquire)){*age=seen?now-at:UINT32_MAX;return seen;}}
 *age=UINT32_MAX;return false;
}
static bool valid_control(int16_t volume){return volume==INT16_MIN||(volume>=-60*256&&volume<=0);}
static void set_control(int16_t volume,bool mute,bool network){state.control.volume_q8=volume;state.control.mute=mute;state.control.from_network=network;if(network)usb_control_notify_mark(7);state.control.revision++;gain_target=mute||volume==INT16_MIN?0:volume==0?1:powf(10.f,volume/(20.f*256));gain_remaining=state.rate/1000;if(!gain_remaining)gain_remaining=1;gain_step=(gain_target-gain_current)/gain_remaining;publish();}
bool usb_microphone_set_control(int16_t volume,bool mute){if(!valid_control(volume))return false;set_control(volume,mute,false);return true;}
bool usb_microphone_request_control(int16_t volume,bool mute){if(!valid_control(volume))return false;atomic_store_explicit(&control_mailbox,0x80000000u|((uint32_t)mute<<16)|(uint16_t)volume,memory_order_release);return true;}
void usb_microphone_get_control(usb_mic_control_t *out){critical_section_enter_blocking(&lock);*out=snapshot.control;critical_section_exit(&lock);}
static void reset_transition(void){staged_bytes=staged_at=staged_committed=0;staged_fallback=false;transition_gap=false;transition_last=transition_anchor=0;transition_remaining=0;}
static int32_t read_sample(const uint8_t *p,unsigned stride){return stride==2?(int16_t)(p[0]|(uint16_t)p[1]<<8):(int32_t)((uint32_t)p[0]<<8|(uint32_t)p[1]<<16|(uint32_t)p[2]<<24)>>8;}
static unsigned write_frames(const uint8_t *source,unsigned bytes,unsigned stride,bool gap,bool fallback,unsigned *committed){
 if(committed)*committed=0;
 if(!staged_bytes){
  unsigned frames=bytes/stride;if(frames>USB_MIC_MAX_BYTES/stride)frames=USB_MIC_MAX_BYTES/stride;if(!frames)return 0;
  mic_dsp_state_t work={gain_current,gain_remaining,transition_last,transition_anchor,transition_remaining,transition_gap};
  unsigned ramp=state.rate*2/1000;if(!ramp)ramp=1;
  if(work.gap!=gap){work.gap=gap;work.anchor=work.last;work.remaining=ramp;}
  for(unsigned i=0;i<frames;i++){
   if(work.gain_remaining){work.gain+=gain_step;if(!--work.gain_remaining)work.gain=gain_target;}
   int32_t target=gap?0:(int32_t)(read_sample(source+i*stride,stride)*work.gain),value=target;
   if(work.remaining){--work.remaining;value=(int32_t)(target+(work.anchor-(float)target)*(work.remaining/(float)ramp));}
   uint8_t *o=gain_scratch+i*stride;for(unsigned b=0;b<stride;b++)o[b]=(uint8_t)((uint32_t)value>>(8*b));
   work.last=value;staged_states[i]=work;
  }
  staged_bytes=frames*stride;staged_at=staged_committed=0;staged_stride=stride;staged_gap=gap;staged_fallback=fallback;staged_revision=state.control.revision;
 }
 // The caller drains one staged block before changing source or gap mode.
 if(staged_stride!=stride||staged_gap!=gap)return 0;
 unsigned wrote=tud_audio_write(gain_scratch+staged_at,(uint16_t)(staged_bytes-staged_at));
 staged_at+=wrote;unsigned done=staged_at/stride;
 if(done>staged_committed){
  mic_dsp_state_t *s=&staged_states[done-1];gain_current=s->gain;
  if(staged_revision==state.control.revision)gain_remaining=s->gain_remaining;
  else if(gain_remaining)gain_step=(gain_target-gain_current)/gain_remaining;
  transition_last=s->last;transition_anchor=s->anchor;transition_remaining=s->remaining;transition_gap=s->gap;
  if(staged_fallback)state.fallback_concealed_frames+=done-staged_committed;
  if(committed)*committed=done-staged_committed;staged_committed=done;
 }
 if(staged_at==staged_bytes)staged_bytes=staged_at=staged_committed=0;
 return wrote;
}
static unsigned write_processed(const uint8_t *source,unsigned bytes,unsigned stride){return write_frames(source,bytes,stride,false,false,NULL);}
static unsigned write_concealed(unsigned frames,unsigned stride){unsigned done=0;write_frames(NULL,frames*stride,stride,true,false,&done);return done;}
static void write_fallback(unsigned frames,unsigned stride){write_frames(NULL,frames*stride,stride,true,true,NULL);}
void usb_microphone_init(void){critical_section_init(&lock);state.formats_mask=USB_MIC_DEFAULT_MASK;state.rate=48000;state.bits=16;state.generation=1;state.last_underrun_enqueue_age_us=UINT32_MAX;atomic_store(&accept_generation,1);atomic_store(&accept_rate,48000);atomic_store(&accept_bits,16);ready=true;publish();}
void usb_microphone_get_status(usb_mic_status_t*s){critical_section_enter_blocking(&lock);*s=snapshot;critical_section_exit(&lock);}
void usb_microphone_reset(void){if(!ready)return;atomic_store(&accepting,false);state.active=state.primed=false;state.alternate_setting=0;state.generation++;atomic_store(&accept_generation,state.generation);pending_valid=false;reset_transition();task_seen=false;state.fifo_bytes=0;publish();}
bool usb_microphone_set_formats(uint32_t mask){if(!usb_mic_mask_valid(mask)||state.active)return false;state.formats_mask=mask;unsigned i=usb_mic_format_index(mask,0);state.rate=usb_mic_formats[i].rate;state.bits=usb_mic_formats[i].bits;atomic_store(&accept_rate,state.rate);atomic_store(&accept_bits,state.bits);publish();return true;}
bool usb_microphone_set_alt(uint8_t alt){unsigned i=usb_mic_format_index(state.formats_mask,alt);if(i==USB_MIC_FORMAT_COUNT)return false;usb_microphone_reset();state.rate=usb_mic_formats[i].rate;state.bits=usb_mic_formats[i].bits;state.alternate_setting=alt;state.active=alt!=0;atomic_store(&accept_rate,state.rate);atomic_store(&accept_bits,state.bits);atomic_store(&accepting,state.active);atomic_store(&tx_bytes,0);atomic_store(&tx_packets,0);tud_audio_clear_ep_in_ff();silence_packet=silence_remainder=0;flow_threshold=(uint16_t)((state.rate/1000)*(state.bits/8));if(alt)tud_audio_n_set_ep_in_fifo_threshold(0,flow_threshold);publish();return true;}
bool usb_microphone_enqueue(const uint8_t*pcm,uint16_t frames,uint32_t rate,uint8_t bits,uint32_t generation,uint16_t gap){
 if(!atomic_load(&accepting)||!pcm||!frames||frames>(rate+999)/1000||frames*(bits/8)>USB_MIC_MAX_BYTES||generation!=atomic_load(&accept_generation)||rate!=atomic_load(&accept_rate)||bits!=atomic_load(&accept_bits)){atomic_fetch_add(&stale_packets,1);return false;}
 unsigned w=atomic_load_explicit(&wr,memory_order_relaxed),r=atomic_load_explicit(&rd,memory_order_acquire);if(w-r==MIC_PACKETS){atomic_fetch_add(&queue_drops,1);return false;}
 atomic_fetch_add_explicit(&enqueue_version,1,memory_order_acq_rel);
 mic_packet_t*p=&packets[w%MIC_PACKETS];p->generation=generation;p->bytes=frames*(bits/8);p->gap_frames=gap;memcpy(p->pcm,pcm,p->bytes);atomic_fetch_add(&queued_bytes,p->bytes);atomic_fetch_add(&received_frames,frames);
 uint32_t at=time_us_32();bool enabled=atomic_load_explicit(&network_enabled,memory_order_relaxed);if(enabled&&atomic_load_explicit(&enqueue_seen,memory_order_relaxed)){uint32_t gap_us=at-atomic_load_explicit(&enqueue_at_us,memory_order_relaxed);if(gap_us>atomic_load_explicit(&enqueue_maximum_gap_us,memory_order_relaxed))atomic_store_explicit(&enqueue_maximum_gap_us,gap_us,memory_order_relaxed);}atomic_store_explicit(&enqueue_at_us,at,memory_order_relaxed);atomic_store_explicit(&enqueue_seen,enabled,memory_order_relaxed);
 atomic_store_explicit(&wr,w+1,memory_order_release);atomic_fetch_add_explicit(&enqueue_version,1,memory_order_release);return true;
}
void usb_microphone_tx_isr(uint16_t bytes){atomic_fetch_add_explicit(&tx_bytes,bytes,memory_order_relaxed);atomic_fetch_add_explicit(&tx_packets,1,memory_order_relaxed);}
void usb_microphone_task(void){
 uint32_t start=time_us_32();unsigned control=atomic_exchange_explicit(&control_mailbox,0,memory_order_acq_rel);if(control&0x80000000u)set_control((int16_t)control,(control&0x10000u)!=0,true);if(!state.active){task_seen=false;publish();return;}uint32_t task_gap=task_seen?start-last_task_us:0;last_task_us=start;task_seen=true;if(task_gap>state.maximum_task_gap_us)state.maximum_task_gap_us=task_gap;tu_fifo_t*ff=tud_audio_get_ep_in_ff();unsigned stride=state.bits/8,nominal=(state.rate+999)/1000*stride;
 if(!ff)return;state.fifo_bytes=tu_fifo_count(ff);if(state.fifo_bytes>state.peak_fifo_bytes)state.peak_fifo_bytes=state.fifo_bytes;
 unsigned reset=atomic_load_explicit(&network_reset_serial,memory_order_acquire);
 if(reset!=observed_network_reset){observed_network_reset=reset;discard_before=atomic_load(&network_discard_before);if(!pending_valid||!staged_bytes||!staged_gap)pending_gap=0;pending_valid=false;state.primed=false;}
 // Discard obsolete packets without resetting producer-owned indices.
 for(unsigned n=0;n<4;n++){
  unsigned r=atomic_load_explicit(&rd,memory_order_relaxed),w=atomic_load_explicit(&wr,memory_order_acquire);if(r==w)break;
  unsigned obsolete=discard_before-r;
  if(packets[r%MIC_PACKETS].generation==state.generation&&(!obsolete||obsolete>MIC_PACKETS))break;atomic_fetch_sub(&queued_bytes,packets[r%MIC_PACKETS].bytes);atomic_store_explicit(&rd,r+1,memory_order_release);atomic_fetch_add(&stale_packets,1);
 }
 unsigned target=state.rate*USB_MIC_TARGET_MS/1000*stride;
 // Finish the one partially accepted block before switching modes, including a
 // source stop between bytes of a packed sample. Discard other old source data.
 if(staged_bytes&&!pending_valid){unsigned done=0;write_frames(NULL,0,stride,staged_gap,staged_fallback,&done);if(pending_gap){state.concealed_frames+=done;pending_gap-=done;}if(!staged_bytes)pending_gap=0;}
 if(!atomic_load(&network_enabled))state.primed=false;
 unsigned obsolete_queued=discard_before-atomic_load_explicit(&rd,memory_order_relaxed);
 if(!state.primed&&atomic_load(&network_enabled)&&(!obsolete_queued||obsolete_queued>MIC_PACKETS)&&atomic_load(&queued_bytes)>=target)state.primed=true;
 unsigned wanted_threshold=state.primed?target:state.rate/1000*stride;if(wanted_threshold!=flow_threshold){flow_threshold=(uint16_t)wanted_threshold;tud_audio_n_set_ep_in_fifo_threshold(0,flow_threshold);}
 if(state.primed&&(!staged_bytes||pending_valid)){
  for(unsigned n=0;n<4;n++){
   if(!pending_valid){unsigned r=atomic_load_explicit(&rd,memory_order_relaxed),w=atomic_load_explicit(&wr,memory_order_acquire);if(r==w)break;pending=packets[r%MIC_PACKETS];atomic_fetch_sub(&queued_bytes,pending.bytes);atomic_store_explicit(&rd,r+1,memory_order_release);if(pending.generation!=state.generation){atomic_fetch_add(&stale_packets,1);continue;}pending_valid=true;pending_at=0;pending_gap=pending.gap_frames;if(pending_gap==UINT16_MAX){tud_audio_clear_ep_in_ff();reset_transition();transition_gap=true;state.primed=false;pending_gap=0;break;}}
   unsigned free=tu_fifo_remaining(ff)/stride*stride;if(free<nominal)break;
   if(pending_gap){unsigned frames=pending_gap;if(frames>USB_MIC_MAX_BYTES/stride)frames=USB_MIC_MAX_BYTES/stride;unsigned done=write_concealed(frames,stride);pending_gap-=done;state.concealed_frames+=done;continue;}
   unsigned left=pending.bytes-pending_at;if(left>free)left=free;pending_at+=write_processed(pending.pcm+pending_at,left,stride);if(pending_at==pending.bytes)pending_valid=false;
  }
 }
 unsigned available=tu_fifo_count(ff);if(available<nominal*2){
  if(state.primed&&!pending_valid&&atomic_load(&wr)==atomic_load(&rd)){state.primed=false;state.underruns++;state.last_underrun_us64=time_us_64();state.last_underrun_fifo_bytes=available;state.last_underrun_target_bytes=target;state.last_underrun_task_gap_us=task_gap;state.last_underrun_enqueue_timing_valid=enqueue_age((uint32_t)state.last_underrun_us64,&state.last_underrun_enqueue_age_us);state.last_underrun_queued_packets=atomic_load(&wr)-atomic_load(&rd);state.last_underrun_pending_bytes=pending_valid?pending.bytes-pending_at:0;state.last_underrun_staged_bytes=staged_bytes-staged_at;state.last_underrun_tx_packets=atomic_load(&tx_packets);}
  // Host recording stays clocked during startup/no source. These are explicit
  // synthetic zero frames, not samples falsely labelled as microphone data.
  uint32_t packet=atomic_load(&tx_packets);if(packet!=silence_packet&&!staged_bytes){uint32_t elapsed_packets=packet-silence_packet;silence_packet=packet;if(elapsed_packets>2)silence_remainder=0;silence_remainder+=state.rate;unsigned zero_frames=silence_remainder/1000;silence_remainder%=1000;write_fallback(zero_frames,stride);}
 }
 unsigned elapsed=time_us_32()-start;if(elapsed>state.task_max_us)state.task_max_us=elapsed;publish();
}
