#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_audio_packet.c"
#include "../uab_audio_fec.c"
int main(void){
  uab_audio_fec_t f;uab_audio_fec_clear(&f);uint8_t data[4][816];size_t lengths[4];
  usb_audio_block_t block={0};block.sample_rate=44100;block.sample_format=1;block.generation=7;
  for(unsigned n=0;n<4;n++){
    block.frames=n==3?45:44;block.sample_index=n*44;memset(block.pcm,(int)n+1,block.frames*4);
    lengths[n]=uab_audio_encode(data[n],sizeof(data[n]),&block,123,7,n,456);assert(lengths[n]);
    assert(uab_audio_fec_push(&f,data[n],lengths[n],1000+n)==(n==3));
  }
  assert(f.ready&&f.length<=1200&&f.packet[5]==4&&f.max_length==lengths[3]);
  for(unsigned missing=0;missing<4;missing++){
    uint8_t repair[816];memcpy(repair,f.packet+40,f.max_length);
    for(unsigned n=0;n<4;n++)if(n!=missing)for(unsigned i=0;i<lengths[n];i++)repair[i]^=data[n][i];
    assert(!memcmp(repair,data[missing],lengths[missing]));
  }
  assert(!uab_audio_fec_push(&f,data[3],lengths[3],2000));
  uab_audio_fec_clear(&f);data[0][15]=8;assert(!uab_audio_fec_push(&f,data[0],lengths[0],1));assert(f.epoch==8&&f.mask==1);
  puts("PASS: production XOR-4 variable-length PCM parity, every single loss, duplicates and generation isolation");return 0;
}
