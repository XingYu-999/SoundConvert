#include <math.h>
#include <stdatomic.h>
#include <string.h>
#include "pico/stdlib.h"
#include "pico/critical_section.h"
#include "tusb.h"
#include "device/dcd.h"
#include "usb_audio.h"
#include "usb_descriptors.h"
#include "usb_host_control.h"
#include "usb_control_notify.h"

#define BLOCK_COUNT 8u
#define VOLUME_MIN (-60 * 256)
#define VOLUME_MAX 0
// Host scalar APIs can produce fractional dB. Preserve the full UAC1 Q8.8
// resolution instead of rejecting otherwise valid Windows volume writes.
#define VOLUME_STEP 1
static usb_audio_status_t current, snapshot;
static critical_section_t status_lock;
static atomic_uint requested; // 0=no request, 1=stop, 2=start
static atomic_uint configured_formats=1, requested_internal_volume;
static atomic_uint lifecycle_pending, rx_bytes, rx_fault, generation;
static atomic_uint write_index, read_index;
static atomic_uint expired_frames, obsolete_frames;
static atomic_uint last_rx_us, rx_max_gap_us, rx_last_packet_bytes;
static atomic_uint queue_peak_blocks,block_age_max_us;
static usb_audio_block_t blocks[BLOCK_COUNT];
static bool initialized, alt_active, primed;
static uint32_t stop_at, deadline, last_service, consumed_bytes, received_origin;
static uint32_t service_gap_us;
static uint64_t sample_index;
static unsigned frame_remainder;static uint8_t microphone_resume_alt;
static float target_gain = 1.0f, applied_gain = 1.0f;
static bool fifo_phase_waiting;
static uint32_t fifo_phase_wait_started;
static uint16_t frame_bytes(void) { return (uint16_t)(current.bits/8*2); }
static uint16_t block_bytes(void) { return (uint16_t)((current.sample_rate+999)/1000*frame_bytes()); }
static void select_format(uint8_t alt) {
  unsigned index=usb_format_index(current.formats_mask?current.formats_mask:1,alt);
  if(index==USB_FORMAT_COUNT)index=0;
  current.alternate_setting=alt;
  current.sample_rate=usb_formats[index].rate;
  current.bits=usb_formats[index].bits;
  current.sample_format=usb_format_code(current.bits);
}

static void publish(void) {
  current.rx_bytes = atomic_load_explicit(&rx_bytes, memory_order_relaxed);
  current.expired_frames = atomic_load_explicit(&expired_frames, memory_order_relaxed);
  current.obsolete_frames = atomic_load_explicit(&obsolete_frames, memory_order_relaxed);
  current.rx_max_gap_us=atomic_load_explicit(&rx_max_gap_us,memory_order_relaxed);
  current.rx_last_packet_bytes=atomic_load_explicit(&rx_last_packet_bytes,memory_order_relaxed);
  current.queue_peak_blocks=atomic_load_explicit(&queue_peak_blocks,memory_order_relaxed);
  current.block_age_max_us=atomic_load_explicit(&block_age_max_us,memory_order_relaxed);
  if(initialized){audio_feedback_state_t feedback;uint32_t irq=save_and_disable_interrupts();bool have=tud_audio_n_get_feedback_state(0,&feedback);restore_interrupts(irq);
    if(have){current.feedback_q16=feedback.value_q16;current.feedback_average_bytes=feedback.fifo_average_bytes;current.feedback_threshold_bytes=feedback.fifo_threshold_bytes;
      if(current.streaming){if(feedback.value_q16<current.feedback_min_q16)current.feedback_min_q16=feedback.value_q16;if(feedback.value_q16>current.feedback_max_q16)current.feedback_max_q16=feedback.value_q16;}}}
  critical_section_enter_blocking(&status_lock);
  snapshot = current;
  critical_section_exit(&status_lock);
}
static void change_state(usb_audio_state_t state) {
  if (current.state != state) { current.state=state; ++current.revision; }
  publish();
}
static void invalidate(void) {
  current.streaming = false;
  primed = false;
  fifo_phase_waiting = false;
  sample_index = 0;
  frame_remainder=0;
  // Restart from silence after a timeline break; gain reaches the Host setting
  // over the first 1 ms block instead of exposing a discontinuous full-scale edge.
  applied_gain = 0;
  current.generation = atomic_fetch_add_explicit(&generation, 1, memory_order_acq_rel) + 1;
  current.feedback_min_q16=UINT32_MAX;current.feedback_max_q16=0;memset(current.fifo_generation_histogram,0,sizeof(current.fifo_generation_histogram));
  // Prevent an OUT completion between the FIFO clear and its accounting baseline.
  uint32_t irq = save_and_disable_interrupts();
  if (initialized) tud_audio_clear_ep_out_ff();
  received_origin = atomic_load_explicit(&rx_bytes, memory_order_relaxed);
  consumed_bytes = 0;
  atomic_store_explicit(&rx_fault, 0, memory_order_relaxed);
  atomic_store_explicit(&last_rx_us,0,memory_order_relaxed);
  restore_interrupts(irq);
}
static void update_gains(void) {
  // One master control is applied once to both PCM channels. Per-channel FU
  // controls are not advertised or accepted, preventing Windows UAC1 doubling.
  target_gain = (current.mute[0] || current.internal_mute) ? 0.0f :
    powf(10.0f, current.volume_q8[0] / (256.0f*20.0f)) * current.internal_volume_percent / 100.0f;
  ++current.control_revision;
}
void usb_audio_init(void) {
  critical_section_init(&status_lock);
  usb_microphone_init();
  memset(&current, 0, sizeof(current));
  current.state=USB_AUDIO_STOPPED;
  current.revision=1;
  current.internal_volume_percent=80;
  current.internal_volume_revision=1;
  atomic_store(&requested_internal_volume,0);
  update_gains();
  applied_gain=target_gain;
  current.min_consume_fifo_bytes=UINT32_MAX;
  select_format(0);
  publish();
}
void usb_audio_set_enabled(bool enabled) {
  atomic_store_explicit(&requested, enabled ? 2 : 1, memory_order_release);
}
void usb_audio_get_status(usb_audio_status_t *status) {
  critical_section_enter_blocking(&status_lock);
  *status = snapshot;
  critical_section_exit(&status_lock);
}
const char *usb_audio_get_state_name(usb_audio_state_t state) {
  static const char *const names[] = {"STOPPED", "STARTING", "RUNNING", "STOPPING", "FAULT"};
  return (unsigned)state <= USB_AUDIO_FAULT ? names[state] : "FAULT";
}
bool usb_audio_validate_name(const char *name) { return usb_descriptors_validate_name(name); }
bool usb_audio_set_names(const char *product, const char *manufacturer, const char *playback) {
  critical_section_enter_blocking(&status_lock);
  bool result = snapshot.state == USB_AUDIO_STOPPED &&
    atomic_load_explicit(&requested, memory_order_acquire) != 2 &&
    usb_descriptors_set_names(product, manufacturer, playback);
  critical_section_exit(&status_lock);
  return result;
}
bool usb_audio_apply_descriptor_options(const usb_descriptor_options_t *options){
 static usb_descriptor_set_t staging;
 if(snapshot.state!=USB_AUDIO_STOPPED||atomic_load(&requested)==2||!usb_descriptors_build(options,&staging))return false;
 if(!usb_microphone_set_formats(options->capture_formats_mask)||!usb_descriptors_commit(&staging))return false;
 atomic_store_explicit(&configured_formats,options->formats_mask,memory_order_release);return true;
}
bool usb_audio_set_capture_formats(uint32_t mask){
 if(snapshot.state!=USB_AUDIO_STOPPED||atomic_load(&requested)==2||!usb_mic_mask_valid(mask))return false;
 if(!usb_microphone_set_formats(mask))return false;usb_descriptors_set_capture_formats(mask);return true;
}
bool usb_audio_set_hi_res_enabled(bool enabled) {
  return usb_audio_set_formats(usb_formats_legacy(enabled));
}
bool usb_audio_set_formats(uint32_t mask) {
  if(!usb_formats_valid(mask))return false;
  critical_section_enter_blocking(&status_lock);
  bool result=snapshot.state==USB_AUDIO_STOPPED &&
    atomic_load_explicit(&requested,memory_order_acquire)!=2;
  if(result){usb_descriptors_set_formats(mask);atomic_store_explicit(&configured_formats,mask,memory_order_release);}
  critical_section_exit(&status_lock);
  return result;
}
bool usb_audio_set_internal_volume(unsigned percent,bool mute){
  if(percent>100)return false;
  atomic_store_explicit(&requested_internal_volume,0x80000000u|((uint32_t)mute<<8)|percent,memory_order_release);return true;
}
void usb_audio_get_names(char *product, char *manufacturer, char *playback) {
  critical_section_enter_blocking(&status_lock);
  usb_descriptors_get_names(product, manufacturer, playback);
  critical_section_exit(&status_lock);
}
bool usb_audio_read_block(usb_audio_block_t *block) {
  for (unsigned attempts=0; attempts<BLOCK_COUNT; ++attempts) {
    unsigned rd=atomic_load_explicit(&read_index, memory_order_relaxed);
    unsigned wr=atomic_load_explicit(&write_index, memory_order_acquire);
    if (rd == wr) return false;
    unsigned depth=wr-rd;if(depth>atomic_load_explicit(&queue_peak_blocks,memory_order_relaxed))atomic_store_explicit(&queue_peak_blocks,depth,memory_order_relaxed);
    *block=blocks[rd % BLOCK_COUNT];
    atomic_store_explicit(&read_index, rd+1, memory_order_release);
    if (block->generation != atomic_load_explicit(&generation, memory_order_acquire))
      atomic_fetch_add_explicit(&obsolete_frames, block->frames, memory_order_relaxed);
    else {uint32_t age=time_us_32()-block->captured_us;if(age>atomic_load_explicit(&block_age_max_us,memory_order_relaxed))atomic_store_explicit(&block_age_max_us,age,memory_order_relaxed);
      if(age>UAB_AUDIO_BLOCK_DEADLINE_US)atomic_fetch_add_explicit(&expired_frames,block->frames,memory_order_relaxed);else return true;}
  }
  return false;
}
unsigned usb_audio_queued_blocks(void) {
  unsigned rd=atomic_load_explicit(&read_index,memory_order_relaxed);
  unsigned wr=atomic_load_explicit(&write_index,memory_order_acquire);
  return wr-rd;
}
static bool consume(uint32_t now) {
  usb_audio_block_t block;
  unsigned accumulated=frame_remainder+current.sample_rate;
  uint16_t stride=frame_bytes(), wanted=(uint16_t)(accumulated/1000*stride);
  uint32_t irq=save_and_disable_interrupts(),irq_started=time_us_32();
  uint16_t available=tud_audio_available();
  uint32_t received=atomic_load_explicit(&rx_bytes, memory_order_relaxed)-received_origin;
  bool fault=atomic_exchange_explicit(&rx_fault, 0, memory_order_relaxed) ||
    available % stride || received-consumed_bytes != available;
  // A digital bridge has no local DAC clock to satisfy. Some Hosts keep a
  // shallow FIFO despite explicit feedback. Do not destroy a continuous USB
  // timeline merely because its next packet is slightly behind our CPU timer.
  // Leave every partial sample in the FIFO and re-anchor the 1 ms phase once
  // the next complete source block arrives. A real stop is still bounded.
  if(!fault && available<wanted){
    if(!fifo_phase_waiting){fifo_phase_waiting=true;fifo_phase_wait_started=now;current.fifo_phase_waits++;}
    if((uint32_t)(now-fifo_phase_wait_started)<2000){restore_interrupts(irq);return false;}
  }
  if(fifo_phase_waiting && !fault && available>=wanted){
    uint32_t waited=now-fifo_phase_wait_started;
    if(waited>current.fifo_phase_wait_max_us)current.fifo_phase_wait_max_us=waited;
    fifo_phase_waiting=false;deadline=now;
  }
  uint16_t bytes=fault ? 0 : tud_audio_read(block.pcm, wanted);
  consumed_bytes+=bytes;
  if(available<current.min_consume_fifo_bytes)current.min_consume_fifo_bytes=available;
  if(!fault&&bytes<wanted){uint32_t received_at=atomic_load_explicit(&last_rx_us,memory_order_relaxed);current.last_underrun_available_bytes=available;current.last_underrun_read_bytes=bytes;current.last_underrun_wanted_bytes=wanted;current.last_underrun_at_us=time_us_32();current.last_underrun_rx_gap_us=received_at?current.last_underrun_at_us-received_at:UINT32_MAX;current.last_underrun_service_gap_us=service_gap_us;}
  uint32_t irq_duration=time_us_32()-irq_started;
  restore_interrupts(irq);
  if(irq_duration>current.irq_section_max_us)current.irq_section_max_us=irq_duration;
  // Core 0 owns the 64-bit timer read. Keep it outside the USB IRQ section;
  // the queue slot remains immutable until Core 1 publishes read_index.
  block.extract_us64=time_us_64();
  unsigned histogram_bin=(unsigned)((uint32_t)available*1000/(current.sample_rate*stride));if(histogram_bin>15)histogram_bin=15;current.fifo_generation_histogram[histogram_bin]++;
  if (available > current.peak_fifo) current.peak_fifo=available;
  if (fault || bytes % stride) { ++current.resyncs; invalidate(); return false; }
  // A short USB block cannot be labelled as a complete, continuous 1 ms source
  // interval. End this generation and re-prime instead of joining stale PCM.
  if (bytes < wanted) { ++current.underruns; ++current.resyncs; invalidate(); return false; }
  block.frames=bytes/stride;
  frame_remainder=accumulated%1000;
  block.sample_rate=current.sample_rate;
  block.sample_format=current.sample_format;
  block.sample_index=sample_index;
  block.generation=current.generation;
  block.captured_us=now;
  sample_index+=block.frames;
  current.consumed_frames+=block.frames;
  // Host and internal gain are combined once. Changes use a 1 ms ramp; after
  // the transition, a muted block is exactly zero (also for packed 24-bit PCM).
  if(applied_gain==1.0f&&target_gain==1.0f){current.unity_gain_blocks++;}
  else if(applied_gain==0.0f&&target_gain==0.0f){memset(block.pcm,0,bytes);current.muted_blocks++;}
  else {current.scaled_blocks++;for (unsigned i=0; i<block.frames; ++i) {
    float gain = applied_gain +
      (target_gain-applied_gain)*(float)(i+1)/(float)block.frames;
    for (unsigned ch=0; ch<2; ++ch) {
      uint8_t*p=block.pcm+i*stride+ch*(stride/2);
      int32_t input;
      if(stride==4)input=(int16_t)((uint16_t)p[0]|((uint16_t)p[1]<<8));
      else{input=(int32_t)((uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16));if(input&0x800000)input|=(int32_t)0xff000000;}
      int32_t value=(int32_t)lrintf((float)input*gain),maximum=stride==4?32767:8388607,minimum=-maximum-1;
      if(value>maximum)value=maximum;if(value<minimum)value=minimum;
      p[0]=(uint8_t)value;p[1]=(uint8_t)((uint32_t)value>>8);if(stride==6)p[2]=(uint8_t)((uint32_t)value>>16);
    }
  }}
  applied_gain=target_gain;
  unsigned wr=atomic_load_explicit(&write_index, memory_order_relaxed);
  unsigned rd=atomic_load_explicit(&read_index, memory_order_acquire);
  if (wr-rd == BLOCK_COUNT) { current.queue_drops+=block.frames; return true; }
  blocks[wr % BLOCK_COUNT]=block;
  atomic_store_explicit(&write_index, wr+1, memory_order_release);
  return true;
}
void usb_audio_task(void) {
  uint32_t now=time_us_32();
  if(current.state==USB_AUDIO_STOPPED){current.formats_mask=atomic_load_explicit(&configured_formats,memory_order_acquire);current.hi_res_enabled=(current.formats_mask&~1u)!=0;select_format(0);}
  unsigned internal=atomic_exchange_explicit(&requested_internal_volume,0,memory_order_acq_rel);
  if(internal&0x80000000u){current.internal_volume_percent=(uint8_t)internal;current.internal_mute=(internal&0x100u)!=0;++current.internal_volume_revision;update_gains();}
  if (last_service && now-last_service > current.max_service_us) current.max_service_us=now-last_service;
  if (last_service && current.streaming && current.state == USB_AUDIO_RUNNING &&
      now-last_service > current.max_stream_service_us) current.max_stream_service_us=now-last_service;
  service_gap_us=last_service?now-last_service:0;
  last_service=now;
  unsigned command=atomic_exchange_explicit(&requested, 0, memory_order_acq_rel);
  if (command == 1 && current.state != USB_AUDIO_STOPPED && current.state != USB_AUDIO_STOPPING) {
    change_state(USB_AUDIO_STOPPING);
    if (initialized && !tud_disconnect()) change_state(USB_AUDIO_FAULT);
    else { alt_active=false; select_format(0); invalidate(); usb_microphone_reset();stop_at=now; }
  } else if (command == 2 && current.state == USB_AUDIO_STOPPED) {
    change_state(USB_AUDIO_STARTING);
    invalidate();
    bool ok;
    if (!initialized) {
      tusb_rhport_init_t init={.role=TUSB_ROLE_DEVICE, .speed=TUSB_SPEED_FULL};
      ok=tusb_init(0, &init);
      initialized=ok;
    } else ok=tud_connect();
    change_state(ok ? USB_AUDIO_RUNNING : USB_AUDIO_FAULT);
  }
  if (atomic_exchange_explicit(&lifecycle_pending, 0, memory_order_acq_rel)) {
    usb_host_control_task(false);
    alt_active=false; select_format(0); current.mounted=false; current.suspended=false; invalidate();usb_microphone_reset();microphone_resume_alt=0;
  }
  // Apply pending bus invalidation before dispatch: SET_CONFIGURATION/SET_INTERFACE
  // in the same batch must be allowed to establish the new active generation.
  if (initialized) tud_task_ext(0, false);
  if(current.state==USB_AUDIO_RUNNING&&current.mounted&&!current.suspended)usb_control_notify_task();else usb_control_notify_reset();
  usb_host_control_task(current.state==USB_AUDIO_RUNNING&&current.mounted&&!current.suspended);
  if (current.state == USB_AUDIO_STOPPING) {
    if (now-stop_at >= 300000 && !tud_task_event_ready()) {
      current.mounted=false; current.suspended=false; invalidate();usb_microphone_reset();microphone_resume_alt=0; change_state(USB_AUDIO_STOPPED);
    } else if (now-stop_at >= 1000000) change_state(USB_AUDIO_FAULT);
  }
  if (current.state == USB_AUDIO_RUNNING && alt_active && current.mounted && !current.suspended) {
    if (!primed && tud_audio_available() >= block_bytes()*2) {
      primed=true; current.streaming=true; deadline=now+1000;
    }
    if (primed) {
      if (!fifo_phase_waiting && (int32_t)(now-deadline) >= 2000) { ++current.resyncs; invalidate(); }
      else for (unsigned n=0; n<2 && primed && (int32_t)(now-deadline)>=0; ++n) {
        if(!consume(now))break;
        deadline+=1000;
      }
    }
  }
  current.fifo_bytes=initialized ? tud_audio_available() : 0;
  publish();
  usb_microphone_task();
}
void tud_mount_cb(void) { current.mounted=true; }
void tud_umount_cb(void) { usb_host_control_task(false);current.mounted=false; alt_active=false; select_format(0); invalidate(); }
void tud_suspend_cb(bool remote_wakeup_en) {
  usb_host_control_task(false);
  usb_mic_status_t mic;usb_microphone_get_status(&mic);microphone_resume_alt=mic.alternate_setting;usb_microphone_reset();tud_audio_clear_ep_in_ff();
  (void)remote_wakeup_en; current.suspended=true; invalidate();
}
void tud_resume_cb(void) {
  if(microphone_resume_alt){usb_microphone_set_alt(microphone_resume_alt);microphone_resume_alt=0;} current.suspended=false; invalidate(); }
void tud_event_hook_cb(uint8_t rhport, uint32_t eventid, bool in_isr) {
  (void)rhport; (void)in_isr;
  if (eventid == DCD_EVENT_BUS_RESET_START || eventid == DCD_EVENT_BUS_RESET_END ||
      eventid == DCD_EVENT_UNPLUGGED) atomic_store_explicit(&lifecycle_pending, 1, memory_order_release);
}
bool tud_audio_rx_done_isr(uint8_t rhport, uint16_t bytes, uint8_t func_id, uint8_t ep, uint8_t alt) {
  (void)rhport; (void)func_id; (void)ep;
  uint32_t received_at=time_us_32(),previous=atomic_exchange_explicit(&last_rx_us,received_at,memory_order_relaxed);
  if(previous&&received_at-previous>atomic_load_explicit(&rx_max_gap_us,memory_order_relaxed))atomic_store_explicit(&rx_max_gap_us,received_at-previous,memory_order_relaxed);
  atomic_store_explicit(&rx_last_packet_bytes,bytes,memory_order_relaxed);
  atomic_fetch_add_explicit(&rx_bytes, bytes, memory_order_relaxed);
  unsigned index=usb_format_index(current.formats_mask,alt);
  if (index==USB_FORMAT_COUNT || bytes % (usb_formats[index].bits/8*2) || tud_audio_available() >= USB_AUDIO_FIFO_BYTES)
    atomic_store_explicit(&rx_fault, 1, memory_order_relaxed);
  return true;
}
void tud_audio_feedback_params_cb(uint8_t func_id, uint8_t alt, audio_feedback_params_t *params) {
  (void)func_id;(void)alt;
  params->method=AUDIO_FEEDBACK_METHOD_FIFO_COUNT;
  // TinyUSB prepares feedback after ANY streaming SET_INTERFACE, including
  // the microphone. Its alt is not the speaker's format index. The playback
  // callback has already committed current.sample_rate/bits at this point.
  params->sample_freq=current.sample_rate;
  params->fifo_count.fifo_threshold=uab_usb_feedback_threshold(current.sample_rate,frame_bytes());
}
bool tud_audio_set_itf_cb(uint8_t rhport, tusb_control_request_t const *request) {
  (void)rhport;
  if(request->wIndex==USB_MIC_STREAMING_ITF)return current.state==USB_AUDIO_RUNNING&&usb_microphone_set_alt((uint8_t)request->wValue);
  if (request->wIndex != USB_AUDIO_STREAMING_ITF || usb_format_index(current.formats_mask,request->wValue)==USB_FORMAT_COUNT ||
      current.state != USB_AUDIO_RUNNING) return false;
  select_format((uint8_t)request->wValue);
  invalidate();
  alt_active=request->wValue != 0;
  return true;
}
bool tud_audio_set_itf_close_ep_cb(uint8_t rhport, tusb_control_request_t const *request) {
  (void)rhport;if(request->wIndex==USB_MIC_STREAMING_ITF){usb_microphone_reset();return true;}
  alt_active=false; select_format(0); invalidate(); return true;
}
static bool ep_request(tusb_control_request_t const *r, bool input) {
  return current.state == USB_AUDIO_RUNNING && r->bmRequestType == (input ? 0xa2 : 0x22) &&
    (r->wIndex == USB_AUDIO_EP_OUT || r->wIndex == USB_MIC_EP_IN) && r->wValue == (AUDIO10_EP_CTRL_SAMPLING_FREQ << 8);
}
bool tud_audio_set_req_ep_cb(uint8_t rhport, tusb_control_request_t const *r, uint8_t *buf) {
  (void)rhport;usb_mic_status_t mic;usb_microphone_get_status(&mic);
  return ep_request(r, false) && r->bRequest == AUDIO10_CS_REQ_SET_CUR && r->wLength == 3 &&
    ((uint32_t)buf[0] | ((uint32_t)buf[1]<<8) | ((uint32_t)buf[2]<<16)) == (r->wIndex==USB_MIC_EP_IN?mic.rate:current.sample_rate);
}
bool tud_audio_get_req_ep_cb(uint8_t rhport, tusb_control_request_t const *r) {
  if (!ep_request(r, true) || r->bRequest != AUDIO10_CS_REQ_GET_CUR) return false;
  usb_mic_status_t mic;usb_microphone_get_status(&mic);uint32_t rate=r->wIndex==USB_MIC_EP_IN?mic.rate:current.sample_rate;
  uint8_t frequency[3]={(uint8_t)rate,(uint8_t)(rate>>8),(uint8_t)(rate>>16)};
  return tud_audio_buffer_and_schedule_control_xfer(rhport, r, frequency, sizeof(frequency));
}
static bool entity_request(tusb_control_request_t const *r, bool input) {
  return current.state == USB_AUDIO_RUNNING && r->bmRequestType == (input ? 0xa1 : 0x21) &&
    (r->wIndex == (USB_AUDIO_FEATURE_UNIT << 8 | USB_AUDIO_CONTROL_ITF) || (usb_profile_has_capture(usb_descriptors_active_profile())&&r->wIndex == (USB_MIC_FEATURE_UNIT << 8 | USB_AUDIO_CONTROL_ITF))) &&
    (r->wValue & 0xff) == 0;
}
bool tud_audio_set_req_entity_cb(uint8_t rhport, tusb_control_request_t const *r, uint8_t *buf) {
  (void)rhport;
  if (!entity_request(r, false) || r->bRequest != AUDIO10_CS_REQ_SET_CUR) return false;
  unsigned channel=r->wValue & 0xff, selector=r->wValue >> 8;
  if(r->wIndex>>8==USB_MIC_FEATURE_UNIT){usb_mic_control_t c;usb_microphone_get_control(&c);
    if(selector==AUDIO10_FU_CTRL_MUTE&&r->wLength==1&&buf[0]<=1)return usb_microphone_set_control(c.volume_q8,buf[0]!=0);
    if(selector==AUDIO10_FU_CTRL_VOLUME&&r->wLength==2)return usb_microphone_set_control((int16_t)(buf[0]|(uint16_t)buf[1]<<8),c.mute);
    return false;
  }
  if (selector == AUDIO10_FU_CTRL_MUTE && r->wLength == 1 && buf[0] <= 1) {
    current.mute[channel]=buf[0] != 0;
  } else if (selector == AUDIO10_FU_CTRL_VOLUME && r->wLength == 2) {
    int16_t value=(int16_t)(buf[0] | ((uint16_t)buf[1] << 8));
    if (value < VOLUME_MIN || value > VOLUME_MAX || value % VOLUME_STEP) return false;
    current.volume_q8[channel]=value;
  } else return false;
  update_gains(); return true;
}
bool tud_audio_get_req_entity_cb(uint8_t rhport, tusb_control_request_t const *r) {
  #if UAB_USB_CONTROL_NOTIFY
  if(current.state==USB_AUDIO_RUNNING&&r->bmRequestType==0xa1&&r->bRequest==0xff&&r->wValue==0&&r->wLength>0&&((r->wIndex==0x200)||(usb_profile_has_capture(usb_descriptors_active_profile())&&r->wIndex==0x700))){uint8_t entity=(uint8_t)(r->wIndex>>8),data[2]={usb_control_notify_pending(entity)?0x80:0,entity};bool ok=tud_audio_buffer_and_schedule_control_xfer(rhport,r,data,2);if(ok)usb_control_notify_ack(entity);return ok;}
#endif
  if (!entity_request(r, true)) return false;
  unsigned channel=r->wValue & 0xff, selector=r->wValue >> 8;
  if(r->wIndex>>8==USB_MIC_FEATURE_UNIT){usb_mic_control_t c;usb_microphone_get_control(&c);
    if(selector==AUDIO10_FU_CTRL_MUTE&&r->bRequest==AUDIO10_CS_REQ_GET_CUR&&r->wLength==1){uint8_t mute=c.mute;return tud_audio_buffer_and_schedule_control_xfer(rhport,r,&mute,1);}
    if(selector!=AUDIO10_FU_CTRL_VOLUME||r->wLength!=2)return false;int16_t v;
    switch(r->bRequest){case AUDIO10_CS_REQ_GET_CUR:v=c.volume_q8;break;case AUDIO10_CS_REQ_GET_MIN:v=VOLUME_MIN;break;case AUDIO10_CS_REQ_GET_MAX:v=VOLUME_MAX;break;case AUDIO10_CS_REQ_GET_RES:v=VOLUME_STEP;break;default:return false;}
    uint8_t data[2]={(uint8_t)v,(uint8_t)((uint16_t)v>>8)};return tud_audio_buffer_and_schedule_control_xfer(rhport,r,data,2);
  }
  if (selector == AUDIO10_FU_CTRL_MUTE && r->bRequest == AUDIO10_CS_REQ_GET_CUR) {
    uint8_t mute=current.mute[channel];
    return tud_audio_buffer_and_schedule_control_xfer(rhport, r, &mute, 1);
  }
  if (selector != AUDIO10_FU_CTRL_VOLUME) return false;
  int16_t value;
  switch (r->bRequest) {
    case AUDIO10_CS_REQ_GET_CUR: value=current.volume_q8[channel]; break;
    case AUDIO10_CS_REQ_GET_MIN: value=VOLUME_MIN; break;
    case AUDIO10_CS_REQ_GET_MAX: value=VOLUME_MAX; break;
    case AUDIO10_CS_REQ_GET_RES: value=VOLUME_STEP; break;
    default: return false;
  }
  uint8_t data[2]={(uint8_t)value, (uint8_t)((uint16_t)value >> 8)};
  return tud_audio_buffer_and_schedule_control_xfer(rhport, r, data, 2);
}

bool tud_audio_tx_done_isr(uint8_t rhport,uint16_t bytes,uint8_t func,uint8_t ep,uint8_t alt){(void)rhport;(void)func;(void)alt;if(ep==USB_MIC_EP_IN)usb_microphone_tx_isr(bytes);return true;}
