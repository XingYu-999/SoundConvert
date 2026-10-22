#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_audio_packet.c"
#include "../uab_audio_tx.c"
static unsigned checks,calls,sent;static bool writable;static uint8_t sent_packet[624];
#define CHECK(x) do{checks++;assert(x);}while(0)
static bool submit(const uint8_t*p,uint16_t n,void*c){calls++;if(c){uab_audio_tx_clear(c);return false;}if(!writable)return false;memcpy(sent_packet,p,n);sent++;return true;}
int main(void){uab_audio_tx_t tx={0};usb_audio_block_t block={0};block.sample_rate=96000;block.sample_format=2;block.frames=96;block.generation=7;block.captured_us=1000;block.pcm[0]=0x95;
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,42,99));CHECK(tx.length==624);
 CHECK(uab_audio_tx_poll(&tx,1100,7,11,99,submit,NULL)==UAB_TX_WAITING&&tx.length==624&&sent==0);
 CHECK(!uab_audio_tx_prepare(&tx,&block,12,7,43,100));block.pcm[0]=0;
 CHECK(uab_audio_tx_poll(&tx,4000,7,11,99,submit,NULL)==UAB_TX_WAITING&&sent==0);
 writable=true;CHECK(uab_audio_tx_poll(&tx,4999,7,11,99,submit,NULL)==UAB_TX_SENT&&sent==1&&!tx.length);
 CHECK(sent_packet[48]==0x95&&sent_packet[19]==42);unsigned before=calls;
 CHECK(uab_audio_tx_poll(&tx,5000,7,11,99,submit,NULL)==UAB_TX_IDLE&&calls==before&&sent==1);
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,43,99));CHECK(uab_audio_tx_poll(&tx,5001,7,11,99,submit,NULL)==UAB_TX_EXPIRED&&calls==before);
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,44,99));CHECK(uab_audio_tx_poll(&tx,1200,8,11,99,submit,NULL)==UAB_TX_STALE&&calls==before);
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,44,99));CHECK(uab_audio_tx_poll(&tx,1200,7,12,99,submit,NULL)==UAB_TX_STALE);
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,44,99));CHECK(uab_audio_tx_poll(&tx,1200,7,11,100,submit,NULL)==UAB_TX_STALE);
 block.captured_us=0xfffffff0u;CHECK(uab_audio_tx_prepare(&tx,&block,11,7,45,99));CHECK(uab_audio_tx_poll(&tx,32,7,11,99,submit,NULL)==UAB_TX_SENT&&sent==2);
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,46,99));uab_audio_tx_clear(&tx);CHECK(!tx.length);
 CHECK(uab_audio_tx_prepare(&tx,&block,11,7,47,99));CHECK(uab_audio_tx_poll(&tx,32,7,11,99,submit,&tx)==UAB_TX_STALE&&!tx.length&&sent==2);
 printf("PASS: %u production audio TX busy/retry/deadline/ownership/wrap checks\n",checks);return 0;}
