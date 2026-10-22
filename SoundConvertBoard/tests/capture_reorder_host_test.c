#include <assert.h>
#include <stdio.h>
#include "../uab_capture_reorder.h"
static unsigned checks,emitted;static uint16_t gaps[8];static uint8_t first[8];
#define CHECK(x) do{checks++;assert(x);}while(0)
static bool emit(const uint8_t*p,uint16_t frames,uint16_t gap,void*ctx){(void)ctx;assert(frames==48);first[emitted]=p[0];gaps[emitted++]=gap;return true;}
int main(void){uab_capture_reorder_t s;uint8_t pcm[96]={1};uab_capture_reorder_clear(&s);
 CHECK(uab_capture_reorder_push(&s,48,48,pcm,96,100));CHECK(uab_capture_reorder_drain(&s,200,960,4,emit,NULL)==0);
 pcm[0]=2;CHECK(uab_capture_reorder_push(&s,0,48,pcm,96,300));CHECK(uab_capture_reorder_drain(&s,400,960,4,emit,NULL)==2);CHECK(first[0]==2&&first[1]==1&&s.reordered==1&&!s.missing_frames&&!gaps[0]&&!gaps[1]);
 CHECK(!uab_capture_reorder_push(&s,0,48,pcm,96,500)&&s.late==1);
 pcm[0]=3;CHECK(uab_capture_reorder_push(&s,144,48,pcm,96,1000));CHECK(uab_capture_reorder_drain(&s,10999,960,4,emit,NULL)==0);CHECK(uab_capture_reorder_drain(&s,11000,960,4,emit,NULL)==1&&gaps[2]==48&&s.missing_frames==48);
 CHECK(uab_capture_reorder_push(&s,6000,48,pcm,96,12000));CHECK(!uab_capture_reorder_push(&s,6000,48,pcm,96,12001)&&s.duplicates==1);CHECK(uab_capture_reorder_drain(&s,22000,960,4,emit,NULL)==1&&gaps[3]==UINT16_MAX);
 uab_capture_reorder_clear(&s);for(unsigned i=0;i<32;i++)CHECK(uab_capture_reorder_push(&s,(i+1)*48,48,pcm,96,0xfffffff0u));CHECK(!uab_capture_reorder_push(&s,33*48,48,pcm,96,10)&&s.overflows==1);
 emitted=0;CHECK(uab_capture_reorder_drain(&s,0xfffffff0u+10000u,960,1,emit,NULL)==1&&s.missing_frames==48);
 printf("PASS: %u microphone reorder/duplicate/loss/deadline/bounds/wrap checks\n",checks);return 0;}
