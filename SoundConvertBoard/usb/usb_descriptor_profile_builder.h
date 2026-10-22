#pragma once
#include "usb_descriptors.h"
#include "usb_control_notify.h"
#include "usb_descriptor_filter.h"
#if defined(__GNUC__)
__attribute__((used))
#endif
// Six one-bit Consumer usages plus ten constant padding bits; two bytes, no report ID.
static const volatile uint8_t profile_report_template[]={
 0x05,0x0c,0x09,1,0xa1,1,0x05,0x0c,
 0x09,0xcd,0x09,0xb5,0x09,0xb6,0x09,0xe9,0x09,0xea,0x09,0xe2,
 0x15,0,0x25,1,0x75,1,0x95,6,0x81,2,
 0x75,10,0x95,1,0x81,3,0xc0};
static inline bool usb_descriptor_set_validate(const usb_descriptor_set_t *s){
 if(!s||s->length<9||s->length>USB_DESCRIPTOR_CAPACITY||!usb_profile_valid(s->options.device_profile)||s->report_length<8||s->report_length>64)return false;
 const uint8_t *p=s->configuration;bool capture=usb_profile_has_capture(s->options.device_profile);unsigned at=0,itfs=0,seen=0,hid=0;
 if((p[2]|p[3]<<8)!=s->length||p[4]!=(capture?4:3))return false;
 while(at<s->length){unsigned n=p[at];if(n<2||at+n>s->length)return false;const uint8_t *d=p+at;
  if(d[1]==4&&n>=9&&d[3]==0){if(d[2]!=itfs++)return false;}
  if(d[1]==4&&n>=9&&d[5]==3){hid++;if(d[2]!=(capture?3:2))return false;}
  if(d[1]==5&&n>=7){if(d[2]==0x84&&UAB_USB_CONTROL_NOTIFY){seen|=16u;at+=n;continue;}if(d[2]!=1&&d[2]!=0x81&&d[2]!=0x83&&(d[2]!=0x82||!capture))return false;seen|=1u<<(d[2]==1?0:d[2]-0x80);}
  if(d[1]==0x21&&n==9&&(d[7]|d[8]<<8)!=s->report_length)return false;
  at+=n;
 }
 return at==s->length&&itfs==(capture?4u:3u)&&hid==1&&seen==((capture?15u:11u)|(UAB_USB_CONTROL_NOTIFY?16u:0u));
}
static inline bool usb_descriptor_profile_build(const usb_descriptor_options_t *o,usb_descriptor_set_t *s,const uint8_t *source,size_t source_length){
 if(!o||!s||!usb_profile_valid(o->device_profile)||!usb_formats_valid(o->formats_mask)||!usb_mic_mask_valid(o->capture_formats_mask))return false;
 const usb_profile_traits_t *t=usb_profile_traits(o->device_profile);uint8_t filtered[USB_DESCRIPTOR_CAPACITY];
 size_t len=usb_descriptor_filter_duplex(filtered,sizeof(filtered),source,source_length,o->formats_mask,o->capture_formats_mask);if(!len)return false;
 memset(s,0,sizeof(*s));s->options=*o;
 // Identical two-byte Consumer bitmap report in all application collections.
 for(unsigned i=0;i<sizeof(profile_report_template);i++)s->report[i]=profile_report_template[i];s->report[1]=o->hid_consumer_compat?0x0c:t->hid_page;s->report[3]=o->hid_consumer_compat?1:t->hid_usage;s->report_length=sizeof(profile_report_template);
 unsigned itf=0,ac_total=0,ac_header=0;
 for(unsigned at=0;at<len;){uint8_t *d=filtered+at;unsigned n=d[0];bool include=true;
  if(d[1]==4)itf=d[2];
  if(!t->capture&&itf==2)include=false;
  if(itf==0&&d[1]==0x24){
   if(d[2]==1){ac_header=s->length;if(!t->capture){n--;d[0]--;d[7]=1;}}
   else if(d[2]==2||d[2]==3||d[2]==6){if(!t->capture&&(d[3]==6||d[3]==7||d[3]==8))include=false;
    if(d[3]==3){d[4]=(uint8_t)t->output_terminal;d[5]=(uint8_t)(t->output_terminal>>8);d[6]=t->associated?6:0;}
    if(d[3]==6){d[4]=(uint8_t)t->input_terminal;d[5]=(uint8_t)(t->input_terminal>>8);d[6]=t->associated?3:0;}
   }
   if(include)ac_total+=n;
  }
  if(d[1]==4&&itf==3&&!t->capture)d[2]=2;
  if(d[1]==0x21&&n==9){d[7]=(uint8_t)s->report_length;d[8]=0;}
  if(include){if(s->length+n>USB_DESCRIPTOR_CAPACITY)return false;memcpy(s->configuration+s->length,d,n);s->length+=n;}
  // Header shrink changes only copied bytes, never the source traversal.
  at+=(!t->capture&&itf==0&&d[1]==0x24&&d[2]==1)?n+1:n;
 }
 s->configuration[2]=(uint8_t)s->length;s->configuration[3]=(uint8_t)(s->length>>8);s->configuration[4]=t->capture?4:3;
 s->configuration[ac_header+5]=(uint8_t)ac_total;s->configuration[ac_header+6]=(uint8_t)(ac_total>>8);
 return usb_descriptor_set_validate(s);
}
