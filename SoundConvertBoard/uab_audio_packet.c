#include <string.h>
#include "uab_audio_packet.h"
static void put16(uint8_t*p,uint16_t n){p[0]=(uint8_t)(n>>8);p[1]=(uint8_t)n;}
static void put32(uint8_t*p,uint32_t n){put16(p,(uint16_t)(n>>16));put16(p+2,(uint16_t)n);}
static void put64(uint8_t*p,uint64_t n){put32(p,(uint32_t)(n>>32));put32(p+4,(uint32_t)n);}
size_t uab_audio_encode(uint8_t*out,size_t capacity,const usb_audio_block_t*b,uint32_t id,uint32_t epoch,uint32_t sequence,uint64_t token){
    return uab_audio_encode_codec(out,capacity,b,id,epoch,sequence,token,0,NULL);
}
size_t uab_audio_encode_codec(uint8_t*out,size_t capacity,const usb_audio_block_t*b,uint32_t id,uint32_t epoch,uint32_t sequence,uint64_t token,uint8_t codec,uab_ima_state_t*state){
    if(!out||!b||!id||!token)return 0;
    if(!usb_format_supported(b->sample_rate,b->sample_format)||b->frames<b->sample_rate/1000||b->frames>(b->sample_rate+999)/1000)return 0;
    if(codec>1)return 0;
    size_t payload=codec?b->frames+7u:b->frames*2u*(b->sample_format==2?3u:2u),total=48+payload;
    if(total>capacity||total>1200)return 0;
    memset(out,0,48);memcpy(out,"UABA",4);out[4]=codec?2:1;out[5]=48;out[38]=codec;
    put32(out+8,id);put32(out+12,epoch);put32(out+16,sequence);put64(out+20,b->sample_index);
    put32(out+28,b->sample_rate);put16(out+32,b->frames);out[34]=2;out[35]=b->sample_format;
    put16(out+36,(uint16_t)payload);put64(out+40,token);
    if(codec){if(uab_ima_encode(out+48,capacity-48,b->pcm,b->frames,b->sample_format==2?3:2,state)!=payload)return 0;}
    else memcpy(out+48,b->pcm,payload);return total;
}
