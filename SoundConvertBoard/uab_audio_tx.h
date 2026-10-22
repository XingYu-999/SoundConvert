#pragma once
#include <stdbool.h>
#include "uab_audio_packet.h"
typedef struct {
    uint8_t packet[48+USB_AUDIO_MAX_PCM_BYTES];
    uint16_t length;
    uint32_t captured_us,generation,stream_id;
    uint64_t token;
    uab_ima_state_t ima;
} uab_audio_tx_t;
typedef enum { UAB_TX_IDLE,UAB_TX_WAITING,UAB_TX_SENT,UAB_TX_EXPIRED,UAB_TX_STALE } uab_audio_tx_result_t;
typedef bool (*uab_audio_submit_fn)(const uint8_t *data,uint16_t bytes,void *context);
void uab_audio_tx_clear(uab_audio_tx_t *tx);
bool uab_audio_tx_prepare(uab_audio_tx_t *tx,const usb_audio_block_t *block,uint32_t stream_id,uint32_t epoch,uint32_t sequence,uint64_t token);
bool uab_audio_tx_prepare_codec(uab_audio_tx_t *tx,const usb_audio_block_t *block,uint32_t stream_id,uint32_t epoch,uint32_t sequence,uint64_t token,uint8_t codec);
uab_audio_tx_result_t uab_audio_tx_poll(uab_audio_tx_t *tx,uint32_t now_us,uint32_t generation,uint32_t stream_id,uint64_t token,uab_audio_submit_fn submit,void *context);
