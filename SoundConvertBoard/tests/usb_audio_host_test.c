// Runs the production USB owner with simulated time/FIFO. Does not claim IRQ,
// multicore or physical USB timing coverage; those require hardware acceptance.
#include <assert.h>
#include <stdio.h>
#include "../usb/usb_audio.c"
#include "stubs/microphone_stub.h"
static uint32_t fake_time=100;
static uint16_t fake_fifo;
static unsigned init_count, connect_count, disconnect_count, checks;
static uint8_t response[4];
static uint16_t response_size;
#define CHECK(expr) do { ++checks; assert(expr); } while(0)
uint32_t time_us_32(void) { return fake_time; }
uint64_t time_us_64(void) { return UINT64_C(0x100000000)+fake_time; }
bool tusb_init(uint8_t port, const tusb_rhport_init_t *init) {
  (void)port; (void)init; ++init_count; return true;
}
bool tud_connect(void) { ++connect_count; return true; }
bool tud_disconnect(void) { ++disconnect_count; return true; }
void tud_task_ext(uint32_t timeout, bool in_isr) { (void)timeout; (void)in_isr; }
bool tud_task_event_ready(void) { return false; }
bool tud_audio_clear_ep_out_ff(void) { fake_fifo=0; return true; }
uint16_t tud_audio_available(void) { return fake_fifo; }
bool tud_audio_n_get_feedback_state(uint8_t id,audio_feedback_state_t *state){(void)id;state->value_q16=(current.sample_rate/1000)<<16;state->fifo_average_bytes=fake_fifo;state->fifo_threshold_bytes=uab_usb_feedback_threshold(current.sample_rate,current.bits/8*2);state->method=AUDIO_FEEDBACK_METHOD_FIFO_COUNT;return true;}
uint16_t tud_audio_read(void *buffer, uint16_t size) {
  uint16_t bytes=fake_fifo < size ? fake_fifo : size;
  if(current.sample_format==2){uint8_t*p=buffer;for(unsigned i=0;i<bytes/3;i++){int32_t sample=i%2 ? -7000000 : 7000000;p[i*3]=(uint8_t)sample;p[i*3+1]=(uint8_t)((uint32_t)sample>>8);p[i*3+2]=(uint8_t)((uint32_t)sample>>16);}}
  else {int16_t *samples=buffer;for (unsigned i=0;i<bytes/2;++i) samples[i]=i%2 ? -20000 : 20000;}
  fake_fifo-=bytes; return bytes;
}
bool tud_audio_buffer_and_schedule_control_xfer(uint8_t port, const tusb_control_request_t *r, void *data, uint16_t size) {
  (void)port;
  response_size=size < r->wLength ? size : r->wLength;
  memcpy(response, data, response_size); return true;
}
bool usb_descriptors_validate_name(const char *s) { return s && *s; }
bool usb_descriptors_set_names(const char *a, const char *b, const char *c) { return a && b && c; }
void usb_descriptors_set_hi_res_enabled(bool enabled) { (void)enabled; }
void usb_descriptors_set_formats(uint32_t mask) { (void)mask; }
void usb_descriptors_get_names(char *a, char *b, char *c) { (void)a; (void)b; (void)c; }
static void receive(unsigned bytes) {
  fake_fifo=(uint16_t)(fake_fifo+bytes);
  tud_audio_rx_done_isr(0, (uint16_t)bytes, 0, 1, current.alternate_setting);
}
static void step(unsigned us) { fake_time+=us; usb_audio_task(); }
static bool has_amplitude(const usb_audio_block_t *block, int amplitude) {
  for (unsigned i=0; i<block->frames; ++i) {
    int left=block->samples[i*2], right=block->samples[i*2+1];
    if (amplitude==0 && (left!=0 || right!=0)) return false;
    if (left<amplitude-1 || left>amplitude+1 || right< -amplitude-1 || right> -amplitude+1) return false;
  }
  return true;
}
static int32_t sample24(const usb_audio_block_t*b,unsigned sample){const uint8_t*p=b->pcm+sample*3;uint32_t v=(uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16);return (int32_t)(v&0x800000 ? v|0xff000000 : v);}
int main(void) {
  usb_audio_init();
  CHECK(current.state == USB_AUDIO_STOPPED && !initialized);
  CHECK(current.internal_volume_percent==80&&!current.internal_mute&&fabsf(target_gain-.8f)<.00001f);
  CHECK(!usb_audio_set_internal_volume(101,false));
  CHECK(usb_audio_set_internal_volume(100,false));
  step(1000); CHECK(init_count == 0);
  usb_audio_set_enabled(true); step(1);
  CHECK(current.state == USB_AUDIO_RUNNING && init_count == 1);
  CHECK(!usb_audio_set_names("a", "b", "c"));
  tusb_control_request_t rate={0x22, 1, 0x100, 1, 3};
  uint8_t f48000[3]={0x80, 0xbb, 0}, f44100[3]={0x44, 0xac, 0};
  CHECK(tud_audio_set_req_ep_cb(0, &rate, f48000));
  CHECK(!tud_audio_set_req_ep_cb(0, &rate, f44100));
  rate.wLength=2; CHECK(!tud_audio_set_req_ep_cb(0, &rate, f48000));
  rate.wLength=3; rate.wIndex=0x81; CHECK(!tud_audio_set_req_ep_cb(0, &rate, f48000));
  rate.wIndex=1; rate.bmRequestType=0xa2; rate.bRequest=0x81; rate.wLength=1;
  CHECK(tud_audio_get_req_ep_cb(0, &rate) && response_size == 1 && response[0] == 0x80);
  tusb_control_request_t control={0x21, 1, 0x100, 0x200, 1};
  uint8_t data[2]={2, 0};
  CHECK(!tud_audio_set_req_entity_cb(0, &control, data));
  data[0]=1; CHECK(tud_audio_set_req_entity_cb(0, &control, data));
  control.wValue=0x103; CHECK(!tud_audio_set_req_entity_cb(0, &control, data));
  control.wValue=0x100; control.wIndex=0x201; CHECK(!tud_audio_set_req_entity_cb(0, &control, data));
  control.wIndex=0x200; control.wValue=0x200; control.wLength=2;
  data[0]=0; data[1]=1; CHECK(!tud_audio_set_req_entity_cb(0, &control, data));
  data[0]=128; data[1]=0xff; CHECK(tud_audio_set_req_entity_cb(0, &control, data));
  CHECK(current.volume_q8[0]==-128); // fractional -0.5 dB from a Host scalar API
  control.bmRequestType=0xa1;control.bRequest=0x84;
  CHECK(tud_audio_get_req_entity_cb(0,&control)&&response_size==2&&response[0]==1&&response[1]==0);
  control.bmRequestType=0x21;control.bRequest=1;
  data[0]=0; data[1]=0xc4; CHECK(tud_audio_set_req_entity_cb(0, &control, data));
  control.bmRequestType=0xa1; control.bRequest=0x81;
  CHECK(tud_audio_get_req_entity_cb(0, &control) && response[1] == 0xc4);
  control.wValue=0x100; control.bRequest=0x82;
  CHECK(!tud_audio_get_req_entity_cb(0, &control));
  // No channel controls are advertised: reject both GET and SET for L/R,
  // including every volume query Windows could attempt after a cached topology.
  uint32_t control_revision=current.control_revision;
  for (unsigned channel=1; channel<=2; ++channel) {
    control.bmRequestType=0x21; control.bRequest=1;
    control.wValue=(uint16_t)(0x100|channel); control.wLength=1; data[0]=1;
    CHECK(!tud_audio_set_req_entity_cb(0, &control, data));
    control.bmRequestType=0xa1; control.bRequest=0x81;
    CHECK(!tud_audio_get_req_entity_cb(0, &control));
    control.bmRequestType=0x21; control.bRequest=1;
    control.wValue=(uint16_t)(0x200|channel); control.wLength=2; data[0]=0; data[1]=0xec;
    CHECK(!tud_audio_set_req_entity_cb(0, &control, data));
    control.bmRequestType=0xa1;
    for (unsigned request=0x81; request<=0x84; ++request) {
      control.bRequest=(uint8_t)request;
      CHECK(!tud_audio_get_req_entity_cb(0, &control));
    }
  }
  CHECK(current.control_revision==control_revision && !current.mute[1] && !current.mute[2] &&
        current.volume_q8[1]==0 && current.volume_q8[2]==0);
  tud_mount_cb();
  tusb_control_request_t alternate={1, 11, 1, 1, 0};
  CHECK(tud_audio_set_itf_cb(0, &alternate));
  receive(192); receive(192); step(1);
  CHECK(current.streaming);
  step(1000);
  usb_audio_block_t block;
  CHECK(usb_audio_read_block(&block) && block.frames == 48 && block.sample_index == 0);
  CHECK(block.extract_us64==UINT64_C(0x100000000)+block.captured_us);
  CHECK(usb_audio_queued_blocks()==0);
  CHECK(block.samples[0] == 0 && block.samples[1] == 0); // Master mute applies to both channels.
  // A -20 dB master setting must yield 0.1 amplitude, not 0.01 from a second
  // L/R attenuation. Allow the documented one-block gain ramp to complete.
  control=(tusb_control_request_t){0x21,1,0x100,0x200,1}; data[0]=0;
  CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  control.wValue=0x200; control.wLength=2; data[0]=0; data[1]=0xec;
  CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  receive(192); step(1000); CHECK(usb_audio_read_block(&block));
  receive(192); step(1000); CHECK(usb_audio_read_block(&block) && has_amplitude(&block,2000));
  CHECK(usb_audio_set_internal_volume(80,false));step(1);
  receive(192);step(1000);CHECK(usb_audio_read_block(&block));
  receive(192);step(1000);CHECK(usb_audio_read_block(&block)&&has_amplitude(&block,1600));
  CHECK(current.volume_q8[0]==-20*256&&!current.mute[0]);
  CHECK(usb_audio_set_internal_volume(100,false));step(1);
  receive(192);step(1000);CHECK(usb_audio_read_block(&block));
  control_revision=current.control_revision;
  control.wValue=0x201; CHECK(!tud_audio_set_req_entity_cb(0,&control,data));
  control.wValue=0x202; CHECK(!tud_audio_set_req_entity_cb(0,&control,data));
  receive(192); step(1000);
  CHECK(usb_audio_read_block(&block) && has_amplitude(&block,2000) && current.control_revision==control_revision);
  control.wValue=0x100; control.wLength=1; data[0]=1;
  CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  receive(192); step(1000); CHECK(usb_audio_read_block(&block)); // first muted block fades to zero
  receive(192); step(1000); CHECK(usb_audio_read_block(&block) && has_amplitude(&block,0));
  data[0]=0; CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  control.wValue=0x200; control.wLength=2; data[0]=data[1]=0;
  CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  receive(192); step(1000); CHECK(usb_audio_read_block(&block));
  receive(192); step(1000); CHECK(usb_audio_read_block(&block) && has_amplitude(&block,20000));
  // Independent microphone FU dispatch and strict master-only request lengths.
  int16_t playback_before=current.volume_q8[0];tusb_control_request_t mic={0x21,1,0x200,0x700,2};uint8_t mic_db[2]={0,0xfa};
  CHECK(tud_audio_set_req_entity_cb(0,&mic,mic_db));CHECK(fake_mic.control.volume_q8==-6*256&&current.volume_q8[0]==playback_before);
  mic.wValue=0x201;CHECK(!tud_audio_set_req_entity_cb(0,&mic,mic_db));mic.wValue=0x200;mic.wLength=1;CHECK(!tud_audio_set_req_entity_cb(0,&mic,mic_db));
  mic.wLength=2;mic.bmRequestType=0xa1;mic.bRequest=0x81;CHECK(tud_audio_get_req_entity_cb(0,&mic)&&response[1]==0xfa);mic.wLength=1;CHECK(!tud_audio_get_req_entity_cb(0,&mic));
  // Unknown overflow ends the timeline instead of silently presenting continuous audio.
  receive(USB_AUDIO_FIFO_BYTES); step(1000);
  CHECK(current.resyncs > 0 && !current.streaming);
  uint32_t old_generation=current.generation;
  tud_suspend_cb(false); tud_resume_cb();
  CHECK(!current.streaming && current.generation > old_generation && alt_active);
  receive(384); step(1); step(1000);
  CHECK(usb_audio_read_block(&block));
  receive(192); step(1000); fake_time+=4500;
  CHECK(!usb_audio_read_block(&block)); // stale backlog is never replayed
  step(1);
  usb_audio_status_t stats; usb_audio_get_status(&stats);
  CHECK(stats.expired_frames == 48);
  CHECK(tud_audio_set_itf_cb(0, &alternate));
  receive(384); step(1); step(1000);
  tud_suspend_cb(false);
  CHECK(!usb_audio_read_block(&block));
  step(1); usb_audio_get_status(&stats);
  CHECK(stats.obsolete_frames == 48);
  usb_audio_set_enabled(false); step(1);
  CHECK(current.state == USB_AUDIO_STOPPING && disconnect_count == 1);
  CHECK(!usb_audio_set_names("a", "b", "c"));
  step(299999); CHECK(current.state == USB_AUDIO_STOPPING);
  step(1); CHECK(current.state == USB_AUDIO_STOPPED);
  CHECK(usb_audio_set_names("a", "b", "c"));
  tud_resume_cb(); step(1000); CHECK(current.state == USB_AUDIO_STOPPED);
  usb_audio_set_enabled(true); step(1);
  CHECK(current.state == USB_AUDIO_RUNNING && init_count == 1 && connect_count == 1);
  tusb_control_request_t hi_alt={1,11,2,1,0};CHECK(!tud_audio_set_itf_cb(0,&hi_alt));
  CHECK(!usb_audio_set_hi_res_enabled(true));
  usb_audio_set_enabled(false);step(1);step(300000);CHECK(current.state==USB_AUDIO_STOPPED);
  CHECK(usb_audio_set_hi_res_enabled(true));usb_audio_set_enabled(true);step(1);
  CHECK(current.hi_res_enabled&&current.sample_rate==48000&&current.sample_format==1);
  tud_mount_cb();uint32_t before_generation=current.generation;
  CHECK(tud_audio_set_itf_cb(0,&hi_alt));CHECK(current.sample_rate==96000&&current.sample_format==2&&current.bits==24&&current.generation>before_generation);
  audio_feedback_params_t feedback;tud_audio_feedback_params_cb(0,2,&feedback);
  uint32_t speaker_generation=current.generation;tusb_control_request_t microphone_alt={0x01,11,1,2,0};
  CHECK(tud_audio_set_itf_cb(0,&microphone_alt));tud_audio_feedback_params_cb(0,1,&feedback);
  CHECK(feedback.sample_freq==96000&&feedback.fifo_count.fifo_threshold==3456&&current.generation==speaker_generation&&alt_active);
  microphone_alt.wValue=0;CHECK(tud_audio_set_itf_close_ep_cb(0,&microphone_alt));tud_audio_feedback_params_cb(0,0,&feedback);
  CHECK(feedback.sample_freq==96000&&feedback.fifo_count.fifo_threshold==3456&&current.generation==speaker_generation&&alt_active);

  CHECK(feedback.sample_freq==96000&&feedback.fifo_count.fifo_threshold==3456);
  rate=(tusb_control_request_t){0x22,1,0x100,1,3};uint8_t f96000[3]={0x00,0x77,0x01};
  CHECK(tud_audio_set_req_ep_cb(0,&rate,f96000));CHECK(!tud_audio_set_req_ep_cb(0,&rate,f48000));
  rate.bmRequestType=0xa2;rate.bRequest=0x81;CHECK(tud_audio_get_req_ep_cb(0,&rate)&&response_size==3&&!memcmp(response,f96000,3));
  receive(1152);step(1);step(1000);CHECK(usb_audio_read_block(&block)&&block.frames==96&&block.sample_rate==96000&&block.sample_format==2&&block.sample_index==0);
  CHECK(sample24(&block,0)>0&&sample24(&block,0)<7000000&&sample24(&block,190)==7000000&&sample24(&block,191)==-7000000);
  receive(576);step(1000);CHECK(usb_audio_read_block(&block)&&block.sample_index==96&&sample24(&block,0)==7000000&&sample24(&block,1)==-7000000);
  control=(tusb_control_request_t){0x21,1,0x200,0x200,2};data[0]=0;data[1]=0xec;
  CHECK(tud_audio_set_req_entity_cb(0,&control,data));receive(576);step(1000);CHECK(usb_audio_read_block(&block));
  receive(576);step(1000);CHECK(usb_audio_read_block(&block)&&sample24(&block,0)==700000&&sample24(&block,1)==-700000);
  control.wValue=0x100;control.wLength=1;data[0]=1;CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  receive(576);step(1000);CHECK(usb_audio_read_block(&block)&&sample24(&block,0)>0&&sample24(&block,190)==0&&sample24(&block,191)==0);
  receive(576);step(1000);CHECK(usb_audio_read_block(&block)&&sample24(&block,0)==0&&sample24(&block,191)==0);
  before_generation=current.generation;CHECK(tud_audio_set_itf_cb(0,&alternate));
  CHECK(current.sample_rate==48000&&current.sample_format==1&&current.generation>before_generation);
  tud_audio_feedback_params_cb(0,1,&feedback);CHECK(feedback.sample_freq==48000&&feedback.fifo_count.fifo_threshold==1152);
  CHECK(tud_audio_set_itf_cb(0,&hi_alt));receive(1152);step(1);step(1000);CHECK(usb_audio_read_block(&block));
  step(1000);CHECK(usb_audio_read_block(&block));receive(570);before_generation=current.generation;step(1000);
  uint32_t before_underruns=current.underruns;
  CHECK(!usb_audio_read_block(&block)&&current.streaming&&current.generation==before_generation&&fake_fifo==570);
  receive(6);step(100);
  CHECK(usb_audio_read_block(&block)&&block.frames==96&&block.sample_index==192&&current.generation==before_generation);
  CHECK(current.underruns==before_underruns&&current.fifo_phase_waits>0&&current.fifo_phase_wait_max_us==100);
  receive(570);step(1000);CHECK(current.streaming&&fake_fifo==570);step(2000);
  CHECK(!usb_audio_read_block(&block)&&!current.streaming&&current.generation>before_generation&&current.underruns>before_underruns);
  CHECK(current.last_underrun_available_bytes==570&&current.last_underrun_read_bytes==570&&current.last_underrun_wanted_bytes==576);
  CHECK(current.last_underrun_rx_gap_us==3000&&current.last_underrun_service_gap_us==2000);
  step(1);usb_audio_get_status(&stats);CHECK(stats.rx_last_packet_bytes==570&&stats.min_consume_fifo_bytes<=570);
  // A Host whose USB frame clock is 1000 ppm slower than the CPU timer must
  // still produce a continuous sample timeline. No dropping, zero filling or
  // hidden counter reset is permitted while the next packet is arriving.
  CHECK(tud_audio_set_itf_cb(0,&hi_alt));receive(1152);step(1);
  before_generation=current.generation;before_underruns=current.underruns;
  uint32_t phase_resyncs=current.resyncs,phase_waits=current.fifo_phase_waits;
  uint64_t phase_samples=0;
  for(unsigned n=0;n<4000;n++){
    receive(576);step(1001);
    while(usb_audio_read_block(&block)){CHECK(block.sample_index==phase_samples&&block.frames==96);phase_samples+=block.frames;}
  }
  CHECK(phase_samples>=4000u*96u&&current.streaming&&current.generation==before_generation);
  CHECK(current.underruns==before_underruns&&current.resyncs==phase_resyncs&&current.fifo_phase_waits>phase_waits);
  CHECK(!usb_audio_set_internal_volume(101,false));
  uint32_t control_before=current.control_revision,internal_before=current.internal_volume_revision;
  int16_t host_before=current.volume_q8[0];bool host_mute=current.mute[0];
  CHECK(usb_audio_set_internal_volume(80,true));
  CHECK(current.internal_volume_revision==internal_before);step(1);
  CHECK(current.internal_volume_percent==80&&current.internal_mute&&current.internal_volume_revision==internal_before+1&&current.control_revision>control_before);
  CHECK(current.volume_q8[0]==host_before&&current.mute[0]==host_mute);
  control=(tusb_control_request_t){0x21,1,0x100,0x200,1};data[0]=0;CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  control.wValue=0x200;control.wLength=2;data[0]=data[1]=0;CHECK(tud_audio_set_req_entity_cb(0,&control,data));
  CHECK(current.internal_volume_percent==80&&current.internal_mute&&target_gain==0);
  CHECK(usb_audio_set_internal_volume(80,false));step(1);
  CHECK(fabsf(target_gain-.8f)<.00001f);
  receive(576);step(1000);CHECK(usb_audio_read_block(&block));
  receive(576);step(1000);CHECK(usb_audio_read_block(&block)&&sample24(&block,0)==5600000&&sample24(&block,1)==-5600000);
  CHECK(usb_audio_set_internal_volume(0,false));step(1);receive(576);step(1000);CHECK(usb_audio_read_block(&block));
  receive(576);step(1000);CHECK(usb_audio_read_block(&block)&&sample24(&block,0)==0&&sample24(&block,191)==0);
  CHECK(usb_audio_set_internal_volume(80,false));step(1);
  usb_audio_set_enabled(false);step(1);step(300000);CHECK(current.internal_volume_percent==80);
  CHECK(usb_audio_set_internal_volume(100,false));step(1);CHECK(current.internal_volume_percent==100);

  for(unsigned i=0;i<USB_FORMAT_COUNT;i++){
    usb_audio_set_enabled(false);step(1);step(300000);
    CHECK(usb_audio_set_formats(1u<<i));usb_audio_set_enabled(true);step(1);tud_mount_cb();
    CHECK(!usb_audio_set_formats(0)&&!usb_audio_set_formats(USB_FORMAT_ALL+1));
    tusb_control_request_t alt={1,11,1,1,0};CHECK(tud_audio_set_itf_cb(0,&alt));
    CHECK(current.sample_rate==usb_formats[i].rate&&current.bits==usb_formats[i].bits);
    alt.wValue=2;CHECK(!tud_audio_set_itf_cb(0,&alt));
    unsigned stride=current.bits/8*2,maximum=(current.sample_rate+999)/1000;receive(maximum*stride*2);step(1);
    uint64_t total=0;
    for(unsigned ms=0;ms<1000;ms++){
      step(1000);CHECK(usb_audio_read_block(&block));CHECK(block.sample_index==total&&block.frames>=current.sample_rate/1000&&block.frames<=maximum);
      total+=block.frames;
      receive(block.frames*stride);
    }
    CHECK(total==current.sample_rate);
    // A full target FIFO must survive four missing Host millisecond arrivals.
    CHECK(tud_audio_set_itf_cb(0,&(tusb_control_request_t){1,11,1,1,0}));
    receive(uab_usb_feedback_threshold(current.sample_rate,stride));step(1);
    uint32_t generation_before_pause=current.generation;
    for(unsigned pause=0;pause<4;pause++){step(1000);CHECK(usb_audio_read_block(&block));}
    CHECK(current.generation==generation_before_pause&&current.streaming);
  }
  CHECK(current.unity_gain_blocks>0&&current.muted_blocks>0&&current.scaled_blocks>0);
  // Exercise the production unsigned age/deadline and byte-accounting arithmetic across 32-bit wraps.
  CHECK(tud_audio_set_itf_cb(0,&alternate));
  fake_time=UINT32_MAX-2000;step(1);receive(768*2);step(1);
  for(unsigned n=0;n<12;n++){receive(768);step(1000);CHECK(usb_audio_read_block(&block));}
  atomic_store_explicit(&rx_bytes,UINT32_MAX-100,memory_order_relaxed);received_origin=UINT32_MAX-100;consumed_bytes=0;fake_fifo=0;
  invalidate();receive(768*2);step(1);receive(768);step(1000);CHECK(usb_audio_read_block(&block));
  printf("PASS: %u production USB owner/control/FIFO/lifecycle checks\n", checks);
  return 0;
}
