#pragma once
#include <stddef.h>
#include <stdint.h>
#include "usb_audio.h"
#include "uab_ima_adpcm.h"
// Encode the actual USB block without native-struct layout or PCM conversion.
size_t uab_audio_encode(uint8_t *out,size_t capacity,const usb_audio_block_t *block,
                        uint32_t stream_id,uint32_t epoch,uint32_t sequence,uint64_t token);
size_t uab_audio_encode_codec(uint8_t *out,size_t capacity,const usb_audio_block_t *block,
                        uint32_t stream_id,uint32_t epoch,uint32_t sequence,uint64_t token,uint8_t codec,uab_ima_state_t *state);
