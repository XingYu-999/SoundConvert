#include <assert.h>
#include <stdio.h>
#include <math.h>
#include "../uab_audio_packet.c"
static unsigned checks;
#define CHECK(x) do{checks++;assert(x);}while(0)
static void hex(FILE*f,const uint8_t*p,size_t n){for(size_t i=0;i<n;i++)fprintf(f,"%02x",p[i]);}
int main(int argc,char**argv){
 int predictor=100;uint8_t index=0;CHECK(uab_ima_nibble(101,&predictor,&index)==1&&predictor==101&&index==0);
 predictor=-100;index=0;CHECK(uab_ima_nibble(-89,&predictor,&index)==7&&predictor==-89&&index==8);
 CHECK(uab_ima_nibble(-119,&predictor,&index)==15&&predictor==-119&&index==16);
 FILE*f=argc>1?fopen(argv[1],"wb"):NULL;if(argc>1)CHECK(f!=NULL);
 for(unsigned spec=0;spec<USB_FORMAT_COUNT;spec++){
  usb_audio_block_t b={0};uab_ima_state_t state={0};uint8_t wire[816];uint64_t si=0;
  b.sample_rate=usb_formats[spec].rate;b.sample_format=usb_format_code(usb_formats[spec].bits);unsigned bytes=usb_formats[spec].bits/8;
  for(unsigned seq=0;seq<32;seq++){
   b.frames=(uint16_t)(((seq+1)*b.sample_rate/1000)-(seq*b.sample_rate/1000));b.sample_index=si;
   for(unsigned i=0;i<b.frames;i++)for(unsigned ch=0;ch<2;ch++){
    int value=(int)lrint(.05*(bytes==3?8388607:32767)*sin(6.283185307179586*(ch?880:440)*(double)(si+i)/b.sample_rate));
    uint8_t*p=b.pcm+(i*2+ch)*bytes;p[0]=(uint8_t)value;p[1]=(uint8_t)((uint32_t)value>>8);if(bytes==3)p[2]=(uint8_t)((uint32_t)value>>16);
   }
   size_t n=uab_audio_encode_codec(wire,sizeof(wire),&b,1,3,seq,4,1,&state);
   CHECK(n==48+b.frames+7&&wire[4]==2&&wire[38]==1&&wire[39]==0);
   CHECK(wire[50]<=88&&wire[54]<=88&&wire[51]==0&&wire[55]==0);
   CHECK(n<48+b.frames*bytes*2);
   if(f){fprintf(f,"{\"rate\":%u,\"bits\":%u,\"sequence\":%u,\"wire_hex\":\"",b.sample_rate,bytes*8,seq);hex(f,wire,n);fprintf(f,"\",\"pcm_hex\":\"");hex(f,b.pcm,b.frames*bytes*2);fprintf(f,"\"}\n");}
   si+=b.frames;
  }
  CHECK(!uab_audio_encode_codec(wire,48+b.frames+6,&b,1,3,0,4,1,&state));
  CHECK(!uab_audio_encode_codec(wire,sizeof(wire),&b,1,3,0,4,2,&state));
 }
 if(f)fclose(f);printf("PASS: %u IMA encoder known-step, all-format, independent-header and bounds checks\n",checks);return 0;
}
