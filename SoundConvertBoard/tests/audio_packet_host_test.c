#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_audio_packet.c"
static unsigned checks;
#define CHECK(x) do{checks++;assert(x);}while(0)
int main(void){uint8_t packet[816],expected_index[8]={1,2,3,4,5,6,7,8};usb_audio_block_t block={0};
 block.sample_index=0x0102030405060708ULL;block.sample_rate=48000;block.sample_format=1;block.frames=48;
 for(unsigned i=0;i<sizeof(block.pcm);i++)block.pcm[i]=(uint8_t)(i*17+0x80);
 CHECK(uab_audio_encode(packet,sizeof(packet),&block,0x11223344,5,0xfffffffe,0x8877665544332211ULL)==240);
 CHECK(!memcmp(packet,"UABA\x01\x30\x00\x00\x11\x22\x33\x44",12));CHECK(!memcmp(packet+20,expected_index,8));
 CHECK(packet[28]==0&&packet[29]==0&&packet[30]==0xbb&&packet[31]==0x80&&packet[32]==0&&packet[33]==48);
 CHECK(packet[34]==2&&packet[35]==1&&packet[36]==0&&packet[37]==192&&packet[38]==0&&packet[39]==0);
 CHECK(!memcmp(packet+48,block.pcm,192));
 block.sample_rate=96000;block.sample_format=2;block.frames=96;
 CHECK(uab_audio_encode(packet,sizeof(packet),&block,1,2,3,4)==624);
 CHECK(packet[29]==1&&packet[30]==0x77&&packet[31]==0&&packet[33]==96&&packet[35]==2&&packet[36]==2&&packet[37]==64);
 CHECK(!memcmp(packet+48,block.pcm,576));CHECK(!uab_audio_encode(packet,623,&block,1,2,3,4));
 block.frames=95;CHECK(!uab_audio_encode(packet,sizeof(packet),&block,1,2,3,4));block.frames=96;
 block.sample_format=9;CHECK(!uab_audio_encode(packet,sizeof(packet),&block,1,2,3,4));block.sample_format=2;
 CHECK(!uab_audio_encode(packet,sizeof(packet),&block,0,2,3,4));CHECK(!uab_audio_encode(packet,sizeof(packet),&block,1,2,3,0));
 for(unsigned i=0;i<USB_FORMAT_COUNT;i++)for(unsigned ceil=0;ceil<2;ceil++){
   block.sample_rate=usb_formats[i].rate;block.sample_format=usb_format_code(usb_formats[i].bits);block.frames=(uint16_t)((block.sample_rate+(ceil?999:0))/1000);
   unsigned bytes=block.frames*2*(usb_formats[i].bits/8);
   CHECK(uab_audio_encode(packet,sizeof(packet),&block,1,2,3,4)==48+bytes);
   CHECK(packet[35]==block.sample_format&&!memcmp(packet+48,block.pcm,bytes));
 }
 printf("PASS: %u production UABA stereo format/payload/bounds checks\n",checks);return 0;}
