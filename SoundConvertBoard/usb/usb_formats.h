#ifndef UAB_USB_FORMATS_H
#define UAB_USB_FORMATS_H
#include <stdbool.h>
#include <stdint.h>
// Stable bit assignments shared with the client. Legacy profiles remain bits 0/1.
#define USB_FORMAT_COUNT 10u
#define USB_FORMAT_ALL 0x3ffu
typedef struct { uint32_t rate; uint8_t bits; } usb_format_t;
static const usb_format_t usb_formats[USB_FORMAT_COUNT] = {
  {48000,16},{96000,24},{44100,16},{44100,24},{48000,24},
  {88200,16},{88200,24},{96000,16},{176400,16},{192000,16}
};
static inline bool usb_formats_valid(uint32_t mask) { return mask && !(mask & ~USB_FORMAT_ALL); }
static inline uint32_t usb_formats_legacy(bool high) { return high ? 3u : 1u; }
static inline uint8_t usb_format_code(uint8_t bits) { return bits==24 ? 2 : 1; }
static inline unsigned usb_format_index(uint32_t mask, unsigned alt) {
  if(!alt)alt=1;
  for(unsigned i=0;i<USB_FORMAT_COUNT;i++)if(mask&(1u<<i)){if(!--alt)return i;}
  return USB_FORMAT_COUNT;
}
static inline bool usb_format_supported(uint32_t rate, uint8_t code) {
  for(unsigned i=0;i<USB_FORMAT_COUNT;i++)if(usb_formats[i].rate==rate&&usb_format_code(usb_formats[i].bits)==code)return true;
  return false;
}
#endif
