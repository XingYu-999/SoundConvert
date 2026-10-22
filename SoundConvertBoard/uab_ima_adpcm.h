#pragma once
#include <stdint.h>
#include <stddef.h>
// UAB stereo packet format, not WAV IMA or RTP DVI4. Every packet carries
// both initial predictors and step indices so loss never poisons the next.
typedef struct { uint8_t index[2]; } uab_ima_state_t;
static const int uab_ima_steps[89]={7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767};
static const int uab_ima_index[16]={-1,-1,-1,-1,2,4,6,8,-1,-1,-1,-1,2,4,6,8};
static inline uint8_t uab_ima_nibble(int sample,int *predictor,uint8_t *index){
    int step=uab_ima_steps[*index],difference=sample-*predictor,delta=step>>3;uint8_t code=0;
    if(difference<0){code=8;difference=-difference;}
    if(difference>=step){code|=4;difference-=step;delta+=step;}
    if(difference>=(step>>1)){code|=2;difference-=step>>1;delta+=step>>1;}
    if(difference>=(step>>2)){code|=1;delta+=step>>2;}
    int value=*predictor+((code&8)?-delta:delta);*predictor=value>32767?32767:value< -32768?-32768:value;
    int next=*index+uab_ima_index[code];*index=(uint8_t)(next<0?0:next>88?88:next);return code;
}
static inline int uab_ima_input(const uint8_t*p,unsigned bytes){
    if(bytes==2)return (int16_t)((uint16_t)p[0]|((uint16_t)p[1]<<8));
    int32_t value=(int32_t)((uint32_t)p[0]|((uint32_t)p[1]<<8)|((uint32_t)p[2]<<16));
    if(value&0x800000)value|=(int32_t)0xff000000;return value/256;
}
static inline size_t uab_ima_encode(uint8_t*out,size_t capacity,const uint8_t*pcm,unsigned frames,unsigned bytes,uab_ima_state_t*state){
    if(!out||!pcm||!state||!frames||(bytes!=2&&bytes!=3)||capacity<frames+7||state->index[0]>88||state->index[1]>88)return 0;
    int predictor[2];
    for(unsigned ch=0;ch<2;ch++){predictor[ch]=uab_ima_input(pcm+ch*bytes,bytes);uint16_t v=(uint16_t)predictor[ch];out[ch*4]=(uint8_t)v;out[ch*4+1]=(uint8_t)(v>>8);out[ch*4+2]=state->index[ch];out[ch*4+3]=0;}
    for(unsigned frame=1;frame<frames;frame++){
        uint8_t left=uab_ima_nibble(uab_ima_input(pcm+frame*bytes*2,bytes),&predictor[0],&state->index[0]);
        uint8_t right=uab_ima_nibble(uab_ima_input(pcm+(frame*2+1)*bytes,bytes),&predictor[1],&state->index[1]);
        out[7+frame]=(uint8_t)(left|(right<<4));
    }
    return frames+7;
}
