#include "uab_audio_fec.h"
#include <string.h>
static uint32_t r32(const uint8_t*p){return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3];}
static uint64_t r64(const uint8_t*p){return ((uint64_t)r32(p)<<32)|r32(p+4);}
static void w16(uint8_t*p,uint16_t x){p[0]=x>>8;p[1]=x;}
static void w32(uint8_t*p,uint32_t x){p[0]=x>>24;p[1]=x>>16;p[2]=x>>8;p[3]=x;}
static void w64(uint8_t*p,uint64_t x){w32(p,(uint32_t)(x>>32));w32(p+4,(uint32_t)x);}
void uab_audio_fec_clear(uab_audio_fec_t*f){memset(f,0,sizeof(*f));}
bool uab_audio_fec_push(uab_audio_fec_t*f,const uint8_t*p,size_t n,uint32_t now){
  if(!p||n<48||n>48+USB_AUDIO_MAX_PCM_BYTES||memcmp(p,"UABA",4)||(p[4]!=1&&p[4]!=2)||p[5]!=48||p[6]||p[7])return false;
  uint32_t seq=r32(p+16),base=seq&~3u,epoch=r32(p+12),stream=r32(p+8);uint64_t token=r64(p+40);
  if(!f->active||f->base!=base||f->epoch!=epoch||f->stream!=stream||f->token!=token){
    uab_audio_fec_clear(f);f->active=true;f->base=base;f->epoch=epoch;f->stream=stream;f->token=token;
  }
  unsigned slot=seq&3u;if(f->mask&(1u<<slot))return false;
  for(size_t i=0;i<n;i++)f->packet[UAB_FEC_HEADER+i]^=p[i];
  f->lens[slot]=(uint16_t)n;if(n>f->max_length)f->max_length=(uint16_t)n;f->mask|=(uint8_t)(1u<<slot);
  if(f->mask!=15)return false;
  memcpy(f->packet,"UABF",4);f->packet[4]=1;f->packet[5]=4;w16(f->packet+6,UAB_FEC_HEADER);
  w32(f->packet+8,f->stream);w32(f->packet+12,f->epoch);w32(f->packet+16,f->base);w64(f->packet+20,f->token);
  for(unsigned i=0;i<4;i++)w16(f->packet+28+i*2,f->lens[i]);w16(f->packet+36,f->max_length);
  f->length=UAB_FEC_HEADER+f->max_length;f->ready=true;f->ready_us=now;return true;
}
