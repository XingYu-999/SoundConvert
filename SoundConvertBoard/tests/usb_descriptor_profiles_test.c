// Tests the production filter independently from TinyUSB's compiler attributes.
// check_usb_descriptors.py separately inspects the actual linked ARM template.
#include <assert.h>
#include <stdio.h>
#include "../usb/usb_descriptor_filter.h"
int main(void){
  uint8_t source[512]={9,2,0,0,2,1,0,0,0,9,4,0,0,0,1,1,0,0,9,4,1,0,0,1,2,0,0},out[512];unsigned length=27;
  for(unsigned i=0;i<USB_FORMAT_COUNT;i++){
    const usb_format_t*f=usb_formats+i;unsigned packet=((f->rate+999)/1000+1)*f->bits/8*2;
    uint8_t alt[]={9,4,1,(uint8_t)(i+1),2,1,2,0,0,11,0x24,2,1,2,(uint8_t)(f->bits/8),f->bits,1,(uint8_t)f->rate,(uint8_t)(f->rate>>8),(uint8_t)(f->rate>>16),9,5,1,5,(uint8_t)packet,(uint8_t)(packet>>8),1,0,0x81};
    memcpy(source+length,alt,sizeof(alt));length+=sizeof(alt);
  }
  source[2]=(uint8_t)length;source[3]=(uint8_t)(length>>8);
  unsigned profiles=0;
  for(uint32_t mask=1;mask<=USB_FORMAT_ALL;mask++){
    size_t bytes=usb_descriptor_filter(out,sizeof(out),source,length,mask);assert(bytes>0&&(out[2]|((unsigned)out[3]<<8))==bytes);
    unsigned offset=0,alt=0,index=USB_FORMAT_COUNT;
    while(offset<bytes){
      const uint8_t*d=out+offset;assert(d[0]>=2&&offset+d[0]<=bytes);
      if(d[1]==4&&d[2]==1&&d[3]){assert(d[3]==++alt);index=usb_format_index(mask,alt);assert(index<USB_FORMAT_COUNT);}
      if(d[1]==0x24&&d[2]==2&&index<USB_FORMAT_COUNT){assert(d[4]==2&&d[5]==usb_formats[index].bits/8&&d[6]==usb_formats[index].bits);assert(((uint32_t)d[8]|((uint32_t)d[9]<<8)|((uint32_t)d[10]<<16))==usb_formats[index].rate);}
      offset+=d[0];
    }
    assert(offset==bytes&&alt>0&&usb_format_index(mask,alt+1)==USB_FORMAT_COUNT);profiles++;
  }
  assert(!usb_descriptor_filter(out,8,source,length,1));source[27]=0;assert(!usb_descriptor_filter(out,sizeof(out),source,length,1));
  printf("PASS: %u production UAC1 descriptor filter masks, contiguous alternates and stereo PCM\n",profiles);return 0;
}
