#include <string.h>
#include "hardware/flash.h"
#include "hardware/regs/addressmap.h"
#include "pico/flash.h"
#include "usb_audio.h"
#include "uab_config_store.h"

// W5500-EVB-Pico2's W25Q16 is 2 MiB, independent of the generic pico2 default.
#define BOARD_FLASH_BYTES (2u * 1024u * 1024u)
#define SLOT_BYTES 4096u
#define STORE_OFFSET (BOARD_FLASH_BYTES - 2u * SLOT_BYTES)
#define RECORD_BYTES 1024u
#define PAYLOAD_OFFSET 32u
#define LEGACY_PAYLOAD_BYTES 784u
#define PAYLOAD_BYTES 796u
#define COMMIT_OFFSET (SLOT_BYTES - FLASH_PAGE_SIZE)
#define MAGIC 0x43424155u // UABC
#define COMMIT_MAGIC 0x4b4f4241u
extern uint8_t __flash_binary_end[];
static uint8_t record[RECORD_BYTES] __attribute__((aligned(4)));
static uint8_t commit_page[FLASH_PAGE_SIZE] __attribute__((aligned(4)));

static uint32_t get32(const uint8_t *p) {
  return (uint32_t)p[0] | ((uint32_t)p[1]<<8) | ((uint32_t)p[2]<<16) | ((uint32_t)p[3]<<24);
}
static uint64_t get64(const uint8_t *p) { return get32(p) | ((uint64_t)get32(p+4)<<32); }
static void put32(uint8_t *p, uint32_t v) {
  p[0]=(uint8_t)v; p[1]=(uint8_t)(v>>8); p[2]=(uint8_t)(v>>16); p[3]=(uint8_t)(v>>24);
}
static void put64(uint8_t *p, uint64_t v) { put32(p,(uint32_t)v); put32(p+4,(uint32_t)(v>>32)); }
static uint32_t crc32(const uint8_t *data, unsigned length) {
  uint32_t crc=0xffffffff;
  for (unsigned i=0; i<length; ++i) {
    crc^=data[i];
    for (unsigned bit=0; bit<8; ++bit) crc=(crc>>1) ^ (0xedb88320u & (0u-(crc&1u)));
  }
  return ~crc;
}
static const uint8_t *slot_address(unsigned slot) {
  return (const uint8_t *)(XIP_BASE + STORE_OFFSET + slot*SLOT_BYTES);
}
static bool valid_config(const uab_config_t *c) {
  return c && uab_indicator_mode_valid(c->indicator_mode) && usb_profile_valid(c->device_profile) && c->mode <= 1 && c->revision && (!c->formats_mask||usb_formats_valid(c->formats_mask)) &&
    (!c->capture_formats_mask||usb_mic_mask_valid(c->capture_formats_mask)) && memchr(c->product, 0, sizeof(c->product)) && memchr(c->manufacturer, 0, sizeof(c->manufacturer)) &&
    memchr(c->playback, 0, sizeof(c->playback)) &&
    usb_audio_validate_name(c->product) && usb_audio_validate_name(c->manufacturer) &&
    usb_audio_validate_name(c->playback);
}
static void encode(uint8_t *p, const uab_config_t *c) {
  // Explicit encoding avoids struct padding, compiler ABI and endianness dependence.
  memset(p,0,PAYLOAD_BYTES);
  put64(p,c->revision); p+=8;
  memcpy(p,c->product,strlen(c->product)+1); p+=253;
  memcpy(p,c->manufacturer,strlen(c->manufacturer)+1); p+=253;
  memcpy(p,c->playback,strlen(c->playback)+1); p+=253;
  *p++=c->mode;
  memcpy(p,c->ip,4); memcpy(p+4,c->mask,4); memcpy(p+8,c->gateway,4); memcpy(p+12,c->dns,4);
  p[16]=c->hi_res_enabled ? 1 : 0;
  put32(p+17,c->formats_mask?c->formats_mask:usb_formats_legacy(c->hi_res_enabled));
  put32(p+21,c->capture_formats_mask?c->capture_formats_mask:USB_MIC_DEFAULT_MASK);
  p[25]=c->device_profile;p[26]=c->hid_consumer_compat?1:0;p[27]=c->indicator_mode;
}
static bool decode(const uint8_t *p, uab_config_t *c, uint32_t version) {
  memset(c,0,sizeof(*c));
  c->revision=get64(p); p+=8;
  memcpy(c->product,p,253); p+=253;
  memcpy(c->manufacturer,p,253); p+=253;
  memcpy(c->playback,p,253); p+=253;
  c->mode=*p++;
  memcpy(c->ip,p,4); memcpy(c->mask,p+4,4); memcpy(c->gateway,p+8,4); memcpy(c->dns,p+12,4);
  if (version>=2) { if(p[16]>1)return false; c->hi_res_enabled=p[16]!=0; }
  c->formats_mask=version>=3?get32(p+17):usb_formats_legacy(c->hi_res_enabled);
  c->capture_formats_mask=version>=5?get32(p+21):USB_MIC_DEFAULT_MASK;
  c->device_profile=version>=6?p[25]:USB_PROFILE_DESKTOP_SPEAKER;
  if(version>=7&&p[26]>1)return false;c->hid_consumer_compat=version>=7&&p[26]!=0;
  c->indicator_mode=version>=8?p[27]:UAB_INDICATOR_AUTO;
  if(!usb_formats_valid(c->formats_mask)||!usb_mic_mask_valid(c->capture_formats_mask))return false;
  return valid_config(c);
}
static bool inspect(unsigned slot, uint64_t *sequence, uab_config_t *c) {
  const uint8_t *p=slot_address(slot), *commit=p+COMMIT_OFFSET;
  uint32_t version=get32(p+4), bytes=get32(p+16);
  if (get32(p)!=MAGIC || (version!=1 && version!=2 && version!=3 && version!=4 && version!=5 && version!=6 && version!=7 && version!=8) ||
      bytes!=(version==1 ? LEGACY_PAYLOAD_BYTES : version==2 ? 785u : version==3 ? 789u : version==4 ? 790u : version==5 ? 793u : version==6 ? 794u : version==7 ? 795u : PAYLOAD_BYTES) ||
      get32(p+24)!=crc32(p,24) || get32(p+20)!=crc32(p+PAYLOAD_OFFSET,bytes) ||
      get32(commit)!=COMMIT_MAGIC || get64(commit+4)!=get64(p+8) ||
      get32(commit+12)!=get32(p+20) || get32(commit+16)!=crc32(commit,16)) return false;
  if (!decode(p+PAYLOAD_OFFSET,c,version)) return false;
  *sequence=get64(p+8);
  return *sequence != 0;
}
static int latest_slot(uint64_t *sequence, uab_config_t *c) {
  uab_config_t a,b;
  uint64_t sa=0,sb=0;
  bool va=inspect(0,&sa,&a), vb=inspect(1,&sb,&b);
  if (!va && !vb) return -1;
  if (vb && (!va || sb>sa)) { *sequence=sb; *c=b; return 1; }
  *sequence=sa; *c=a; return 0;
}
bool uab_config_load(uab_config_t *config) {
  if (!config) return false;
  uab_config_t loaded;
  uint64_t sequence;
  if (latest_slot(&sequence,&loaded)<0) return false;
  *config=loaded; return true;
}
typedef struct { uint32_t offset; bool commit; } write_request_t;
static void __not_in_flash_func(write_slot)(void *context) {
  const write_request_t *request=context;
  if (request->commit) flash_range_program(request->offset+COMMIT_OFFSET,commit_page,FLASH_PAGE_SIZE);
  else {
    flash_range_erase(request->offset,SLOT_BYTES);
    flash_range_program(request->offset,record,RECORD_BYTES);
  }
}
bool uab_config_save(const uab_config_t *config) {
  if (!valid_config(config) || (uintptr_t)__flash_binary_end > XIP_BASE+STORE_OFFSET) return false;
  usb_audio_status_t usb;
  usb_audio_get_status(&usb);
  if (usb.state != USB_AUDIO_STOPPED) return false;
  uab_config_t existing;
  uint64_t sequence=0;
  int previous=latest_slot(&sequence,&existing);
  if (sequence==UINT64_MAX) return false;
  memset(record,0xff,sizeof(record));
  encode(record+PAYLOAD_OFFSET,config);
  if (previous>=0 && get32(slot_address((unsigned)previous)+4)==8 && !memcmp(slot_address((unsigned)previous)+PAYLOAD_OFFSET,
                            record+PAYLOAD_OFFSET,PAYLOAD_BYTES)) return true;
  put32(record,MAGIC); put32(record+4,8); put64(record+8,sequence+1);
  put32(record+16,PAYLOAD_BYTES);
  put32(record+20,crc32(record+PAYLOAD_OFFSET,PAYLOAD_BYTES));
  put32(record+24,crc32(record,24));
  unsigned next=previous==0 ? 1 : 0;
  write_request_t request={STORE_OFFSET+next*SLOT_BYTES,false};
  if (flash_safe_execute(write_slot,&request,1000)!=PICO_OK) return false;
  // Verify the data before programming a separate one-way commit record.
  if (memcmp(slot_address(next),record,RECORD_BYTES)) return false;
  memset(commit_page,0xff,sizeof(commit_page));
  put32(commit_page,COMMIT_MAGIC); put64(commit_page+4,sequence+1);
  put32(commit_page+12,get32(record+20)); put32(commit_page+16,crc32(commit_page,16));
  request.commit=true;
  if (flash_safe_execute(write_slot,&request,1000)!=PICO_OK) return false;
  uint64_t written;
  return inspect(next,&written,&existing) && written==sequence+1;
}
