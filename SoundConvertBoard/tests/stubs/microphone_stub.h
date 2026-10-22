// Playback-owner tests isolate the new microphone owner; its real queue/FIFO
// implementation is tested independently in usb_microphone_host_test.c.
#include "../../usb/usb_microphone.h"
static usb_mic_status_t fake_mic;
bool usb_microphone_set_control(int16_t volume,bool mute){if(volume!=INT16_MIN&&(volume< -60*256||volume>0))return false;fake_mic.control.volume_q8=volume;fake_mic.control.mute=mute;return true;}
bool usb_microphone_request_control(int16_t v,bool m){return usb_microphone_set_control(v,m);}
void usb_microphone_get_control(usb_mic_control_t *s){*s=fake_mic.control;}
void usb_microphone_init(void){fake_mic=(usb_mic_status_t){.rate=48000,.bits=16,.formats_mask=1,.generation=1};}
bool usb_microphone_set_formats(uint32_t mask){if(!usb_mic_mask_valid(mask))return false;fake_mic.formats_mask=mask;return true;}
bool usb_microphone_set_alt(uint8_t alt){unsigned i=usb_mic_format_index(fake_mic.formats_mask,alt);if(i==USB_MIC_FORMAT_COUNT)return false;fake_mic.active=alt!=0;fake_mic.alternate_setting=alt;fake_mic.rate=usb_mic_formats[i].rate;fake_mic.bits=usb_mic_formats[i].bits;fake_mic.generation++;return true;}
void usb_microphone_reset(void){fake_mic.active=false;fake_mic.alternate_setting=0;fake_mic.generation++;}
void usb_microphone_task(void){}
void usb_microphone_get_status(usb_mic_status_t*s){*s=fake_mic;}
void usb_microphone_tx_isr(uint16_t bytes){(void)bytes;}
void usb_microphone_set_network_enabled(bool enabled){(void)enabled;}
void usb_descriptors_set_capture_formats(uint32_t mask){(void)mask;}

bool tud_audio_clear_ep_in_ff(void){return true;}
// Playback owner tests isolate HID; usb_host_control_host_test runs its production state machine.
void usb_host_control_task(bool usable){(void)usable;}

bool usb_descriptors_build(const usb_descriptor_options_t *o,usb_descriptor_set_t *s){s->options=*o;return usb_profile_valid(o->device_profile)&&usb_formats_valid(o->formats_mask)&&usb_mic_mask_valid(o->capture_formats_mask);}
bool usb_descriptors_commit(const usb_descriptor_set_t *s){usb_descriptors_set_formats(s->options.formats_mask);return true;}
uint8_t usb_descriptors_active_profile(void){return 0;}
