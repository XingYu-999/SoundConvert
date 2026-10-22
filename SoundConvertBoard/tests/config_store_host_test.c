// Exercises the actual encoder, slot selection and commit logic against fake NOR.
#include <assert.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
uint8_t fake_flash[2u*1024u*1024u];
#define __flash_binary_end fake_flash
#define __attribute__(x)
#include "../uab_config_store.c"
static unsigned checks, operations, fail_operation, program_calls;
static bool failed;
static usb_audio_state_t usb_state=USB_AUDIO_STOPPED;
#define CHECK(expr) do { ++checks; assert(expr); } while(0)
void usb_audio_get_status(usb_audio_status_t *s) { memset(s,0,sizeof(*s)); s->state=usb_state; }
bool usb_audio_validate_name(const char *s) { return s && *s; }
void flash_range_erase(uint32_t offset, size_t count) {
  assert(offset>=STORE_OFFSET && offset+count<=sizeof(fake_flash));
  ++operations;
  if (operations==fail_operation) { count/=2; failed=true; }
  memset(fake_flash+offset,0xff,count);
}
void flash_range_program(uint32_t offset, const uint8_t *data, size_t count) {
  if (failed) return;
  assert(offset>=STORE_OFFSET && offset+count<=sizeof(fake_flash));
  ++program_calls; ++operations;
  if (operations==fail_operation) { count=8; failed=true; }
  for (size_t i=0;i<count;++i) fake_flash[offset+i]&=data[i];
}
int flash_safe_execute(void (*callback)(void *), void *arg, uint32_t timeout) {
  (void)timeout; callback(arg); return failed ? -1 : PICO_OK;
}
static void install_legacy_fixture(const uab_config_t*c) {
  uint8_t*p=fake_flash+STORE_OFFSET,*v=p+PAYLOAD_OFFSET,*commit=p+COMMIT_OFFSET;
  memset(p,0xff,SLOT_BYTES);memset(v,0,784);
  put64(v,c->revision);memcpy(v+8,c->product,strlen(c->product)+1);
  memcpy(v+261,c->manufacturer,strlen(c->manufacturer)+1);
  memcpy(v+514,c->playback,strlen(c->playback)+1);v[767]=c->mode;
  memcpy(v+768,c->ip,4);memcpy(v+772,c->mask,4);memcpy(v+776,c->gateway,4);memcpy(v+780,c->dns,4);
  v[784]=0xff; // legacy padding must never be interpreted as enabled
  put32(p,MAGIC);put32(p+4,1);put64(p+8,10);put32(p+16,784);
  put32(p+20,crc32(v,784));put32(p+24,crc32(p,24));
  put32(commit,COMMIT_MAGIC);put64(commit+4,10);put32(commit+12,get32(p+20));put32(commit+16,crc32(commit,16));
}
int main(void) {
  memset(fake_flash,0xff,sizeof(fake_flash));
  uab_config_t original={.revision=1,.product="W5500-EVB-Pico2",.manufacturer="WIZnet",.playback="W5500-EVB-Pico2"};
  uab_config_t loaded={.revision=999};
  CHECK(crc32((const uint8_t *)"123456789",9)==0xcbf43926);
  CHECK(!uab_config_load(&loaded) && loaded.revision==999);
  CHECK(uab_config_save(&original));
  CHECK(uab_config_load(&loaded) && loaded.revision==1 && !strcmp(loaded.product,original.product));
  unsigned before=program_calls;
  CHECK(uab_config_save(&original) && program_calls==before); // identical state never wears flash
  uab_config_t candidate=original; candidate.revision=2; strcpy(candidate.product,"New name");
  for (unsigned fail=1;fail<=3;++fail) {
    operations=0;fail_operation=fail;failed=false;
    CHECK(!uab_config_save(&candidate));
    CHECK(uab_config_load(&loaded) && loaded.revision==1); // erase, data and commit interruption
  }
  fail_operation=0;failed=false;
  CHECK(uab_config_save(&candidate));
  CHECK(uab_config_load(&loaded) && loaded.revision==2 && !strcmp(loaded.product,"New name"));
  fake_flash[STORE_OFFSET+SLOT_BYTES+PAYLOAD_OFFSET+10]^=0x80;
  CHECK(uab_config_load(&loaded) && loaded.revision==1); // CRC fallback to older slot
  usb_state=USB_AUDIO_RUNNING;
  CHECK(!uab_config_save(&candidate));
  usb_state=USB_AUDIO_STOPPED;
  candidate.mode=2; CHECK(!uab_config_save(&candidate));
  candidate.mode=0; memset(candidate.product,'x',sizeof(candidate.product)); CHECK(!uab_config_save(&candidate));
  memset(fake_flash,0xff,sizeof(fake_flash));original.revision=12;original.mode=1;
  strcpy(original.product,"Preserved legacy name");original.ip[0]=10;original.ip[3]=73;
  original.mask[0]=original.mask[1]=original.mask[2]=255;original.gateway[0]=10;original.gateway[3]=1;
  original.dns[0]=9;original.dns[1]=9;original.dns[2]=9;original.dns[3]=9;
  install_legacy_fixture(&original);
  CHECK(uab_config_load(&loaded)&&!loaded.hi_res_enabled&&loaded.revision==12);
  CHECK(!strcmp(loaded.product,original.product)&&!strcmp(loaded.manufacturer,original.manufacturer)&&!strcmp(loaded.playback,original.playback));
  CHECK(loaded.mode==1&&!memcmp(loaded.ip,original.ip,4)&&!memcmp(loaded.mask,original.mask,4)&&!memcmp(loaded.gateway,original.gateway,4)&&!memcmp(loaded.dns,original.dns,4));
  candidate=loaded;candidate.revision=13;candidate.hi_res_enabled=true;candidate.formats_mask=3;CHECK(uab_config_save(&candidate));
  CHECK(uab_config_load(&loaded)&&loaded.hi_res_enabled&&loaded.revision==13&&!strcmp(loaded.product,original.product));
  CHECK(get32(fake_flash+STORE_OFFSET+SLOT_BYTES+4)==8&&get32(fake_flash+STORE_OFFSET+SLOT_BYTES+16)==796);
  before=program_calls;CHECK(uab_config_save(&candidate)&&program_calls==before);
  // Corruption of the new option does not discard the still-valid legacy slot.
  fake_flash[STORE_OFFSET+SLOT_BYTES+PAYLOAD_OFFSET+784]^=1;
  CHECK(uab_config_load(&loaded)&&!loaded.hi_res_enabled&&loaded.revision==12&&!memcmp(loaded.ip,original.ip,4));
  candidate=loaded;candidate.revision=14;candidate.formats_mask=USB_FORMAT_ALL;candidate.hi_res_enabled=true;
  CHECK(uab_config_save(&candidate));CHECK(uab_config_load(&loaded)&&loaded.formats_mask==USB_FORMAT_ALL&&!memcmp(loaded.gateway,original.gateway,4));
  candidate.formats_mask=USB_FORMAT_ALL+1;CHECK(!uab_config_save(&candidate));
  // Version 2 migrates its original high-resolution profile, preserving names/network.
  memset(fake_flash,0xff,sizeof(fake_flash));install_legacy_fixture(&original);
  uint8_t*v2=fake_flash+STORE_OFFSET;v2[PAYLOAD_OFFSET+784]=1;put32(v2+4,2);put32(v2+16,785);put32(v2+20,crc32(v2+PAYLOAD_OFFSET,785));put32(v2+24,crc32(v2,24));
  uint8_t*commit=v2+COMMIT_OFFSET;put32(commit+12,get32(v2+20));put32(commit+16,crc32(commit,16));
  CHECK(uab_config_load(&loaded)&&loaded.formats_mask==3&&loaded.hi_res_enabled&&!strcmp(loaded.product,original.product)&&!memcmp(loaded.dns,original.dns,4));
  for(unsigned version=1;version<=7;version++){
    memset(fake_flash,0xff,sizeof(fake_flash));install_legacy_fixture(&original);
    uint8_t *slot=fake_flash+STORE_OFFSET;unsigned bytes=version==1?784:version==2?785:version==3?789:version==4?790:version==5?793:version==6?794:795;
    put32(slot+4,version);put32(slot+16,bytes);slot[PAYLOAD_OFFSET+793]=0;slot[PAYLOAD_OFFSET+794]=0;slot[PAYLOAD_OFFSET+795]=0xff;slot[PAYLOAD_OFFSET+784]=0;put32(slot+PAYLOAD_OFFSET+785,1);put32(slot+PAYLOAD_OFFSET+789,1);
    put32(slot+20,crc32(slot+PAYLOAD_OFFSET,bytes));put32(slot+24,crc32(slot,24));uint8_t *c=slot+COMMIT_OFFSET;put32(c+12,get32(slot+20));put32(c+16,crc32(c,16));
    CHECK(uab_config_load(&loaded)&&loaded.device_profile==USB_PROFILE_DESKTOP_SPEAKER&&loaded.indicator_mode==UAB_INDICATOR_AUTO);
    loaded.device_profile=USB_PROFILE_HEADSET;loaded.hid_consumer_compat=true;loaded.indicator_mode=UAB_INDICATOR_OFF;CHECK(uab_config_save(&loaded));CHECK(uab_config_load(&loaded)&&loaded.device_profile==USB_PROFILE_HEADSET&&loaded.hid_consumer_compat&&loaded.indicator_mode==UAB_INDICATOR_OFF);
    loaded.device_profile=255;CHECK(!uab_config_save(&loaded));
  }
  loaded.device_profile=USB_PROFILE_HEADSET;
  for(unsigned mode=0;mode<UAB_INDICATOR_MODE_COUNT;++mode){
    loaded.indicator_mode=(uint8_t)mode;loaded.revision++;
    CHECK(uab_config_save(&loaded));CHECK(uab_config_load(&loaded)&&loaded.indicator_mode==mode);
  }
  loaded.indicator_mode=255;CHECK(!uab_config_save(&loaded));
  uint64_t sequence;uab_config_t latest;int newest=latest_slot(&sequence,&latest);
  uint8_t *bad=fake_flash+STORE_OFFSET+(unsigned)newest*SLOT_BYTES;
  bad[PAYLOAD_OFFSET+795]=255;put32(bad+20,crc32(bad+PAYLOAD_OFFSET,PAYLOAD_BYTES));put32(bad+24,crc32(bad,24));
  put32(bad+COMMIT_OFFSET+12,get32(bad+20));put32(bad+COMMIT_OFFSET+16,crc32(bad+COMMIT_OFFSET,16));
  CHECK(uab_config_load(&loaded)&&loaded.indicator_mode==UAB_INDICATOR_BREATHE);
  printf("PASS: %u production config-store CRC/commit/power-loss/rollback checks\n",checks);
  return 0;
}
