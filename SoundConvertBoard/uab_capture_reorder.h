#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#define UAB_CAPTURE_REORDER_SLOTS 32u
typedef struct{bool used;uint64_t index;uint32_t at;uint16_t frames,bytes;uint8_t pcm[288];}uab_capture_slot_t;
typedef struct{uab_capture_slot_t slots[UAB_CAPTURE_REORDER_SLOTS];uint64_t next;uint32_t late,duplicates,overflows,missing_frames,reordered;uint64_t highest;}uab_capture_reorder_t;
typedef bool(*uab_capture_emit_fn)(const uint8_t*,uint16_t,uint16_t,void*);
static inline void uab_capture_reorder_clear(uab_capture_reorder_t*s){memset(s,0,sizeof(*s));}
static inline bool uab_capture_reorder_push(uab_capture_reorder_t*s,uint64_t index,uint16_t frames,const uint8_t*pcm,uint16_t bytes,uint32_t now){
 if(!frames||!pcm||bytes>288||index>UINT64_MAX-frames)return false;
 if(index<s->next){s->late++;return false;}unsigned empty=UAB_CAPTURE_REORDER_SLOTS;
 for(unsigned i=0;i<UAB_CAPTURE_REORDER_SLOTS;i++){if(s->slots[i].used&&s->slots[i].index==index){s->duplicates++;return false;}if(!s->slots[i].used)empty=i;}
 if(empty==UAB_CAPTURE_REORDER_SLOTS){s->overflows++;return false;}if(index<s->highest)s->reordered++;if(index>s->highest)s->highest=index;
 uab_capture_slot_t*p=&s->slots[empty];p->index=index;p->frames=frames;p->bytes=bytes;p->at=now;memcpy(p->pcm,pcm,bytes);p->used=true;return true;
}
static inline unsigned uab_capture_reorder_drain(uab_capture_reorder_t*s,uint32_t now,unsigned max_gap,unsigned budget,uab_capture_emit_fn emit,void*context){
 unsigned sent=0;while(sent<budget){unsigned best=UAB_CAPTURE_REORDER_SLOTS;for(unsigned i=0;i<UAB_CAPTURE_REORDER_SLOTS;i++)if(s->slots[i].used&&(best==UAB_CAPTURE_REORDER_SLOTS||s->slots[i].index<s->slots[best].index))best=i;
 if(best==UAB_CAPTURE_REORDER_SLOTS)break;uab_capture_slot_t*p=&s->slots[best];if(p->index<s->next){p->used=false;s->late++;continue;}
 uint64_t missing=p->index-s->next;if(missing&&(uint32_t)(now-p->at)<10000u)break;
 uint16_t gap=missing>max_gap?UINT16_MAX:(uint16_t)missing;if(!emit(p->pcm,p->frames,gap,context))break;
 s->missing_frames+=(uint32_t)(missing>UINT32_MAX?UINT32_MAX:missing);s->next=p->index+p->frames;p->used=false;sent++;
 }return sent;
}
