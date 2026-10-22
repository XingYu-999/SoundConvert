#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <string.h>
typedef struct {const char *name;uint16_t output_terminal,input_terminal;uint8_t hid_page,hid_usage;bool capture,associated;} usb_profile_traits_t;
enum {USB_PROFILE_DESKTOP_SPEAKER,USB_PROFILE_SPEAKER,USB_PROFILE_HEADPHONES,USB_PROFILE_HEADSET,USB_PROFILE_COUNT};
static const usb_profile_traits_t usb_profiles[USB_PROFILE_COUNT]={
 {"desktop_speaker",0x0304,0x0201,0x0c,1,true,false},
 {"speaker",0x0301,0x0201,0x0c,1,true,false},
 {"headphones",0x0302,0,0x0c,5,false,false},
 {"headset",0x0402,0x0402,0x0b,5,true,true}
};
static inline bool usb_profile_valid(uint8_t p){return p<USB_PROFILE_COUNT;}
static inline const usb_profile_traits_t *usb_profile_traits(uint8_t p){return usb_profile_valid(p)?&usb_profiles[p]:NULL;}
static inline bool usb_profile_has_capture(uint8_t p){return usb_profile_valid(p)&&usb_profiles[p].capture;}
static inline const char *usb_profile_name(uint8_t p){return usb_profile_valid(p)?usb_profiles[p].name:"unknown";}
static inline bool usb_profile_parse(const char *s,uint8_t *p){if(!s||!p)return false;for(uint8_t i=0;i<USB_PROFILE_COUNT;i++)if(!strcmp(s,usb_profiles[i].name)){*p=i;return true;}return false;}
