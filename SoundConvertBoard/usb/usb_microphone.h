#pragma once
#include <stdbool.h>
#include <stdint.h>
#define USB_MIC_FORMAT_COUNT 9u
#define USB_MIC_FORMAT_ALL 0x1ffu
#define USB_MIC_DEFAULT_MASK 1u
#define USB_MIC_MAX_BYTES 288u
#define USB_MIC_FIFO_BYTES 16384u
#define USB_MIC_TARGET_MS 24u
typedef struct {uint32_t rate;uint8_t bits;} usb_mic_format_t;
typedef struct {int16_t volume_q8;bool mute,from_network;uint32_t revision;} usb_mic_control_t;
bool usb_microphone_set_control(int16_t volume_q8,bool mute);
bool usb_microphone_request_control(int16_t volume_q8,bool mute);
void usb_microphone_get_control(usb_mic_control_t *out);
static const usb_mic_format_t usb_mic_formats[USB_MIC_FORMAT_COUNT]={{48000,16},{16000,16},{8000,16},{32000,16},{44100,16},{96000,16},{48000,24},{44100,24},{96000,24}};
static inline bool usb_mic_mask_valid(uint32_t mask){return mask&&!(mask&~USB_MIC_FORMAT_ALL);}
static inline unsigned usb_mic_format_index(uint32_t mask,unsigned alt){if(!alt)alt=1;for(unsigned i=0;i<USB_MIC_FORMAT_COUNT;i++)if(mask&(1u<<i)){if(!--alt)return i;}return USB_MIC_FORMAT_COUNT;}
typedef struct {
 uint32_t formats_mask,rate,generation,received_frames,queued_packets,queue_drops,stale_packets,underruns,concealed_frames,tx_frames,tx_packets,fifo_bytes,peak_fifo_bytes;
 uint8_t bits,alternate_setting;bool active,primed;uint32_t task_max_us;
 uint64_t last_underrun_us64;
 uint32_t last_underrun_fifo_bytes,last_underrun_target_bytes,last_underrun_task_gap_us,last_underrun_enqueue_age_us;
 uint32_t maximum_task_gap_us,maximum_enqueue_gap_us,fallback_concealed_frames;
 uint32_t last_underrun_queued_packets,last_underrun_pending_bytes,last_underrun_staged_bytes,last_underrun_tx_packets;
 bool last_underrun_enqueue_timing_valid;
 usb_mic_control_t control;
}usb_mic_status_t;
// Core0 owns TinyUSB; Core1 submits bounded packet copies only.
void usb_microphone_init(void);
bool usb_microphone_set_formats(uint32_t mask);
bool usb_microphone_set_alt(uint8_t alt);
void usb_microphone_reset(void);
void usb_microphone_task(void);
void usb_microphone_get_status(usb_mic_status_t *status);
bool usb_microphone_enqueue(const uint8_t *pcm,uint16_t frames,uint32_t rate,uint8_t bits,uint32_t generation,uint16_t gap_frames);
void usb_microphone_tx_isr(uint16_t bytes);
void usb_microphone_set_network_enabled(bool enabled);
