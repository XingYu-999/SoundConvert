#ifndef UAB_USB_DESCRIPTOR_FILTER_H
#define UAB_USB_DESCRIPTOR_FILTER_H
#include <stddef.h>
#include <string.h>
#include "usb_formats.h"
#include "usb_microphone.h"
// The immutable template contains contiguous AS alternates in catalog order.
// Filtering keeps the control tree/alt 0 and renumbers selected AS alternates.
static inline size_t usb_descriptor_filter_duplex(uint8_t*out,size_t capacity,const uint8_t*source,size_t length,uint32_t mask,uint32_t mic_mask){
  if(!out||!source||length<9||!usb_formats_valid(mask))return 0;
  size_t at=0,written=0;unsigned alt=0,mic_alt=0;bool include=true;
  while(at<length){
    const uint8_t*d=source+at;unsigned n=d[0];
    if(n<2||n>length-at)return 0;
    bool streaming=n>=9&&d[1]==4&&d[2]==1;bool mic=n>=9&&d[1]==4&&d[2]==2;
    if(n>=9&&d[1]==4&&!streaming&&!mic)include=true;
    if(streaming){if(d[3]>USB_FORMAT_COUNT)return 0;include=d[3]==0||(mask&(1u<<(d[3]-1)));}
    if(mic){if(d[3]>USB_MIC_FORMAT_COUNT)return 0;include=d[3]==0||(mic_mask&(1u<<(d[3]-1)));}
    if(include){if(n>capacity-written)return 0;memcpy(out+written,d,n);if(streaming&&d[3])out[written+3]=(uint8_t)++alt;if(mic&&d[3])out[written+3]=(uint8_t)++mic_alt;written+=n;}
    at+=n;
  }
  if(!alt||written>UINT16_MAX)return 0;
  out[2]=(uint8_t)written;out[3]=(uint8_t)(written>>8);return written;
}
static inline size_t usb_descriptor_filter(uint8_t*out,size_t capacity,const uint8_t*source,size_t length,uint32_t mask){return usb_descriptor_filter_duplex(out,capacity,source,length,mask,USB_MIC_FORMAT_ALL);}
#endif
