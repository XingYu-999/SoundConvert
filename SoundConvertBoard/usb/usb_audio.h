#ifndef SOUND_CONVERT_USB_AUDIO_H
#define SOUND_CONVERT_USB_AUDIO_H
#include <stdbool.h>
#include <stdint.h>
#include "usb_formats.h"
#include "usb_timing.h"
#include "usb_microphone.h"
#include "usb_descriptors.h"

#define USB_AUDIO_RATE 48000u
#define USB_AUDIO_BLOCK_FRAMES 48u
#define USB_AUDIO_MAX_BLOCK_FRAMES 192u
#define USB_AUDIO_MAX_PCM_BYTES 768u
#define USB_AUDIO_FIFO_BYTES UAB_USB_FIFO_BYTES
#define USB_AUDIO_NAME_BYTES 253u
typedef enum {
  USB_AUDIO_STOPPED, USB_AUDIO_STARTING, USB_AUDIO_RUNNING,
  USB_AUDIO_STOPPING, USB_AUDIO_FAULT
} usb_audio_state_t;
typedef struct {
  union { int16_t samples[USB_AUDIO_MAX_PCM_BYTES / 2]; uint8_t pcm[USB_AUDIO_MAX_PCM_BYTES]; };
  uint16_t frames;
  uint32_t sample_rate;
  uint8_t sample_format; // 1=S16LE, 2=S24LE_PACKED; two channels
  uint32_t generation;
  uint64_t sample_index;
  uint32_t captured_us;
  // Extraction-complete software sample point, not the original Host capture time.
  // Published with the complete block through the SPSC release/acquire queue.
  uint64_t extract_us64;
} usb_audio_block_t;
typedef struct {
  usb_audio_state_t state;
  uint32_t revision, generation, rx_bytes, consumed_frames, queue_drops;
  uint32_t underruns, resyncs, peak_fifo, max_service_us, control_revision;
  uint32_t max_stream_service_us;
  uint32_t expired_frames, obsolete_frames;
  uint32_t fifo_bytes;
  uint32_t sample_rate;
  uint8_t sample_format, bits, alternate_setting;
  bool hi_res_enabled;
  uint32_t formats_mask;
  uint32_t rx_max_gap_us, rx_last_packet_bytes, min_consume_fifo_bytes;
  uint32_t last_underrun_available_bytes, last_underrun_read_bytes, last_underrun_wanted_bytes;
  uint32_t last_underrun_rx_gap_us, last_underrun_service_gap_us, last_underrun_at_us;
  bool mounted, streaming, suspended;
  // Only index 0 (master) is supported. Retain array layout for status ABI;
  // indices 1/2 remain false/zero and are not writable UAC controls.
  bool mute[3];
  int16_t volume_q8[3];
  uint8_t internal_volume_percent;
  bool internal_mute;
  uint32_t internal_volume_revision;
  uint32_t feedback_q16,feedback_min_q16,feedback_max_q16;
  uint16_t feedback_average_bytes,feedback_threshold_bytes;
  uint32_t fifo_generation_histogram[16];
  uint32_t irq_section_max_us,unity_gain_blocks,muted_blocks,scaled_blocks;
  uint32_t queue_peak_blocks,block_age_max_us;
  uint32_t fifo_phase_waits,fifo_phase_wait_max_us;
} usb_audio_status_t;

// init/task are Core 0 only. init leaves USB electrically disconnected.
void usb_audio_init(void);
void usb_audio_task(void);
// Thread-safe mailbox; START/STOP completion is reported by get_status.
void usb_audio_set_enabled(bool enabled);
void usb_audio_get_status(usb_audio_status_t *status);
const char *usb_audio_get_state_name(usb_audio_state_t state);
// Core 1 only. Obsolete generations and blocks older than 4 ms are discarded.
bool usb_audio_read_block(usb_audio_block_t *block);
// Core 1: bounded queue-depth snapshot; never consumes or waits for Core 0.
unsigned usb_audio_queued_blocks(void);
// STOPPED only; validates all three names before changing any value.
bool usb_audio_set_names(const char *product, const char *manufacturer, const char *playback);
bool usb_audio_set_hi_res_enabled(bool enabled); // STOPPED and no pending START
bool usb_audio_set_formats(uint32_t mask); // STOPPED and no pending START
bool usb_audio_set_capture_formats(uint32_t mask);
bool usb_audio_apply_descriptor_options(const usb_descriptor_options_t *options);
bool usb_audio_set_internal_volume(unsigned percent, bool mute); // Core 1 -> Core 0 mailbox; runtime only
bool usb_audio_validate_name(const char *name);
void usb_audio_get_names(char *product, char *manufacturer, char *playback);
#endif
