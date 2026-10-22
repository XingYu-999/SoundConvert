#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "usb/usb_audio.h"
#define UAB_FEC_HEADER 40u
#define UAB_FEC_MAX_PACKET (UAB_FEC_HEADER+48u+USB_AUDIO_MAX_PCM_BYTES)
typedef struct{
  uint8_t packet[UAB_FEC_MAX_PACKET];uint16_t length,max_length,lens[4];uint8_t mask;
  uint32_t base,epoch,stream,ready_us;uint64_t token;bool active,ready;
}uab_audio_fec_t;
void uab_audio_fec_clear(uab_audio_fec_t*fec);
bool uab_audio_fec_push(uab_audio_fec_t*fec,const uint8_t*packet,size_t length,uint32_t now);
