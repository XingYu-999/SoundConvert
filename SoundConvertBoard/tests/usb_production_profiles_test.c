// Feed the production filter the bytes extracted from the linked ARM ELF.
#include <assert.h>
#include <stdio.h>
#include "../usb/usb_descriptor_profile_builder.h"
int main(int argc,char**argv){
  assert(argc==2);FILE*file=fopen(argv[1],"rb");assert(file);uint8_t source[2048],out[2048];size_t length=fread(source,1,sizeof(source),file);fclose(file);assert(length>0&&length<sizeof(source));
  unsigned count=0;
  for(uint32_t mask=1;mask<=USB_FORMAT_ALL;mask++){
    size_t bytes=usb_descriptor_filter(out,sizeof(out),source,length,mask);assert(bytes>0);assert((out[2]|((unsigned)out[3]<<8))==bytes);
    unsigned at=0,alt=0,index=USB_FORMAT_COUNT,endpoints=0,itf=0;
    while(at<bytes){uint8_t*d=out+at;assert(d[0]>=2&&at+d[0]<=bytes);
      if(d[1]==4)itf=d[2];
      if(d[1]==4&&d[2]==1&&d[3]){assert(d[3]==++alt);index=usb_format_index(mask,alt);assert(index<USB_FORMAT_COUNT);}
      if(itf==1&&index<USB_FORMAT_COUNT&&d[1]==0x24&&d[2]==2){assert(d[4]==2&&d[5]==usb_formats[index].bits/8&&d[6]==usb_formats[index].bits);assert(((uint32_t)d[8]|((uint32_t)d[9]<<8)|((uint32_t)d[10]<<16))==usb_formats[index].rate);}
      if(itf==1&&d[1]==5){endpoints++;if(d[2]==1){unsigned packet=d[4]|((unsigned)d[5]<<8);assert(packet<=1023&&packet>=(usb_formats[index].rate+999)/1000*usb_formats[index].bits/8*2);assert(d[8]==0x81);}else{assert(d[2]==0x81&&d[4]==3&&d[5]==0);}}
      at+=d[0];
    }
    assert(endpoints==2*alt&&at==bytes);count++;
  }
  usb_descriptor_set_t staging;
  for(unsigned compat=0;compat<2;compat++)for(uint8_t profile=0;profile<USB_PROFILE_COUNT;profile++)for(uint32_t mask=1;mask<=USB_FORMAT_ALL;mask++){
    usb_descriptor_options_t o={mask,USB_MIC_FORMAT_ALL,profile,compat!=0};assert(usb_descriptor_profile_build(&o,&staging,source,length));
    unsigned at=0,itf=0,terminals=0;
    while(at<staging.length){const uint8_t *d=staging.configuration+at;if(d[1]==4)itf=d[2];
      if(itf==0&&d[1]==0x24&&(d[2]==2||d[2]==3)){
        terminals++;if(d[3]==3)assert((d[4]|d[5]<<8)==usb_profiles[profile].output_terminal&&d[6]==(profile==3?6:0));
        if(d[3]==6)assert((d[4]|d[5]<<8)==usb_profiles[profile].input_terminal&&d[6]==(profile==3?3:0));
      }at+=d[0];
    }assert(terminals==(profile==2?2u:4u));assert(staging.report[1]==(compat?0x0c:usb_profiles[profile].hid_page)&&staging.report[3]==(compat?1:usb_profiles[profile].hid_usage));
  }
  unsigned singles=0;
  for(unsigned compat=0;compat<2;compat++)for(uint8_t profile=0;profile<USB_PROFILE_COUNT;profile++)
    for(unsigned play=0;play<USB_FORMAT_COUNT;play++)for(unsigned mic=0;mic<USB_MIC_FORMAT_COUNT;mic++){
      usb_descriptor_options_t o={1u<<play,1u<<mic,profile,compat!=0};assert(usb_descriptor_profile_build(&o,&staging,source,length));
      unsigned at=0,itf=0,alt=0,mics=0;
      while(at<staging.length){const uint8_t*d=staging.configuration+at;
        if(d[1]==4){itf=d[2];alt=d[3];}
        if(itf==2&&alt&&d[1]==0x24&&d[2]==2){mics++;assert(d[4]==1&&d[5]==usb_mic_formats[mic].bits/8&&d[6]==usb_mic_formats[mic].bits);}
        at+=d[0];
      }assert(mics==(profile==USB_PROFILE_HEADPHONES?0u:1u));singles++;
    }
  printf("PASS: four production USB profiles x two HID policies x 1023 playback masks, plus %u single playback/capture combinations\n",singles);
  printf("PASS: %u production ELF -> production filter UAC1 profiles/endpoints/feedback\n",count);return 0;
}
