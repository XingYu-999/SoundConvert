#pragma once
#include <stdint.h>
// A measured Windows Host pause reached 3.883 ms with a 3 ms target.
// Six ms leaves recovery margin; sixteen maximum packets also cover bursts
// at 192 kHz without changing the post-extraction four ms network deadline.
#define UAB_USB_FIFO_BYTES 12352u
#define UAB_USB_FEEDBACK_TARGET_MS 6u
#define UAB_AUDIO_BLOCK_DEADLINE_US 4000u
static inline uint16_t uab_usb_feedback_threshold(uint32_t rate,uint16_t stride){
  return (uint16_t)(((rate+999)/1000)*stride*UAB_USB_FEEDBACK_TARGET_MS);
}
