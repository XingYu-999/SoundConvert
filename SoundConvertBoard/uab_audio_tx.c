#include "uab_audio_tx.h"
void uab_audio_tx_clear(uab_audio_tx_t*tx){tx->length=0;tx->ima.index[0]=tx->ima.index[1]=0;}
bool uab_audio_tx_prepare(uab_audio_tx_t*tx,const usb_audio_block_t*b,uint32_t id,uint32_t epoch,uint32_t sequence,uint64_t token){
    return uab_audio_tx_prepare_codec(tx,b,id,epoch,sequence,token,0);
}
bool uab_audio_tx_prepare_codec(uab_audio_tx_t*tx,const usb_audio_block_t*b,uint32_t id,uint32_t epoch,uint32_t sequence,uint64_t token,uint8_t codec){
    if(tx->length)return false;
    size_t n=uab_audio_encode_codec(tx->packet,sizeof(tx->packet),b,id,epoch,sequence,token,codec,&tx->ima);
    if(!n)return false;
    tx->length=(uint16_t)n;tx->captured_us=b->captured_us;tx->generation=b->generation;tx->stream_id=id;tx->token=token;return true;
}
uab_audio_tx_result_t uab_audio_tx_poll(uab_audio_tx_t*tx,uint32_t now,uint32_t generation,uint32_t id,uint64_t token,uab_audio_submit_fn submit,void*context){
    if(!tx->length)return UAB_TX_IDLE;
    if(tx->generation!=generation||tx->stream_id!=id||tx->token!=token){tx->length=0;return UAB_TX_STALE;}
    if((uint32_t)(now-tx->captured_us)>UAB_AUDIO_BLOCK_DEADLINE_US){tx->length=0;return UAB_TX_EXPIRED;}
    if(!submit(tx->packet,tx->length,context))return tx->length ? UAB_TX_WAITING : UAB_TX_STALE;
    tx->length=0;return UAB_TX_SENT;
}
