#include <string.h>
#include "pico/unique_id.h"
#include "tusb.h"
#include "usb_audio.h"
#include "usb_descriptors.h"
#include "usb_descriptor_profile_builder.h"

// Development-only VID/PID. Distinct revisions avoid cached Windows format
// topology when switching the committed compatibility/high-resolution profile.
#define DEVICE_DESCRIPTOR(revision) { \
  .bLength=sizeof(tusb_desc_device_t), .bDescriptorType=TUSB_DESC_DEVICE, \
  .bcdUSB=0x0200, .bDeviceClass=0, .bDeviceSubClass=0, .bDeviceProtocol=0, \
  .bMaxPacketSize0=64, .idVendor=0xcafe, .idProduct=0x401b, \
  .bcdDevice=revision, .iManufacturer=1, .iProduct=2, .iSerialNumber=3, .bNumConfigurations=1 }
// Descriptor generation 8 uses six explicit HID bits (9 with the optional AC
// status endpoint). Changing bcdDevice invalidates cached old HID topology.
static const tusb_desc_device_t device_descriptor=DEVICE_DESCRIPTOR(UAB_USB_CONTROL_NOTIFY?0x0900:0x0800);

static usb_descriptor_set_t active_set;
uint8_t const*tud_hid_descriptor_report_cb(uint8_t instance){return instance==0?active_set.report:NULL;}
#if UAB_USB_CONTROL_NOTIFY
#define NOTIFY_ENDPOINT 7,5,0x84,3,2,0,8,
#define NOTIFY_ENDPOINT_LEN 7
#else
#define NOTIFY_ENDPOINT
#define NOTIFY_ENDPOINT_LEN 0
#endif
#define AUDIO_CONTROL_LEN (NOTIFY_ENDPOINT_LEN + TUD_AUDIO10_DESC_STD_AC_LEN + TUD_AUDIO10_DESC_CS_AC_LEN(2) + \
  2*TUD_AUDIO10_DESC_INPUT_TERM_LEN + 2*TUD_AUDIO10_DESC_OUTPUT_TERM_LEN + \
  TUD_AUDIO10_DESC_FEATURE_UNIT_LEN(2) + TUD_AUDIO10_DESC_FEATURE_UNIT_LEN(1) + 2*TUD_AUDIO10_DESC_STD_AS_LEN)
#define AUDIO_ALT_LEN (TUD_AUDIO10_DESC_STD_AS_LEN + TUD_AUDIO10_DESC_CS_AS_INT_LEN + \
  TUD_AUDIO10_DESC_TYPE_I_FORMAT_LEN(1) + TUD_AUDIO10_DESC_STD_AS_ISO_EP_LEN + \
  TUD_AUDIO10_DESC_CS_AS_ISO_EP_LEN + TUD_AUDIO10_DESC_STD_AS_ISO_SYNC_EP_LEN)
#define FEATURE_CONTROLS (AUDIO10_FU_CONTROL_BM_MUTE | AUDIO10_FU_CONTROL_BM_VOLUME)
#define AUDIO_CONTROL_DESCRIPTORS \
  TUD_AUDIO10_DESC_STD_AC(0,UAB_USB_CONTROL_NOTIFY,4), \
  TUD_AUDIO10_DESC_CS_AC(0x0100,2*TUD_AUDIO10_DESC_INPUT_TERM_LEN + 2*TUD_AUDIO10_DESC_OUTPUT_TERM_LEN + TUD_AUDIO10_DESC_FEATURE_UNIT_LEN(2) + TUD_AUDIO10_DESC_FEATURE_UNIT_LEN(1),1,2), \
  TUD_AUDIO10_DESC_INPUT_TERM(1,AUDIO_TERM_TYPE_USB_STREAMING,0,2,AUDIO10_CHANNEL_CONFIG_LEFT_FRONT | AUDIO10_CHANNEL_CONFIG_RIGHT_FRONT,0,4), \
  TUD_AUDIO10_DESC_OUTPUT_TERM(3,AUDIO_TERM_TYPE_OUT_DESKTOP_SPEAKER,0,2,4), \
  TUD_AUDIO10_DESC_FEATURE_UNIT(2,1,0,FEATURE_CONTROLS,0,0), \
  TUD_AUDIO10_DESC_INPUT_TERM(6,AUDIO_TERM_TYPE_IN_GENERIC_MIC,0,1,0,0,5), \
  TUD_AUDIO10_DESC_FEATURE_UNIT(7,6,0,FEATURE_CONTROLS,0), \
  TUD_AUDIO10_DESC_OUTPUT_TERM(8,AUDIO_TERM_TYPE_USB_STREAMING,0,7,5), \
  NOTIFY_ENDPOINT \
  TUD_AUDIO10_DESC_STD_AS_INT(1,0,0,4)
#define AUDIO_ALT_DESCRIPTORS(alt,bytes,bits,rate,packet) \
  TUD_AUDIO10_DESC_STD_AS_INT(1,alt,2,4), \
  TUD_AUDIO10_DESC_CS_AS_INT(1,0,AUDIO10_DATA_FORMAT_TYPE_I_PCM), \
  TUD_AUDIO10_DESC_TYPE_I_FORMAT(2,bytes,bits,rate), \
  TUD_AUDIO10_DESC_STD_AS_ISO_EP(USB_AUDIO_EP_OUT,TUSB_XFER_ISOCHRONOUS | TUSB_ISO_EP_ATT_ASYNCHRONOUS,packet,1,USB_AUDIO_EP_FB), \
  TUD_AUDIO10_DESC_CS_AS_ISO_EP(AUDIO10_CS_AS_ISO_DATA_EP_ATT_SAMPLING_FRQ,AUDIO10_CS_AS_ISO_DATA_EP_LOCK_DELAY_UNIT_UNDEFINED,0), \
  TUD_AUDIO10_DESC_STD_AS_ISO_SYNC_EP(USB_AUDIO_EP_FB,0)
#define MIC_ALT_LEN (TUD_AUDIO10_DESC_STD_AS_LEN+TUD_AUDIO10_DESC_CS_AS_INT_LEN+TUD_AUDIO10_DESC_TYPE_I_FORMAT_LEN(1)+TUD_AUDIO10_DESC_STD_AS_ISO_EP_LEN+TUD_AUDIO10_DESC_CS_AS_ISO_EP_LEN)
#define MIC_ALT(alt,bytes,bits,rate,packet) \
 TUD_AUDIO10_DESC_STD_AS_INT(2,alt,1,5), TUD_AUDIO10_DESC_CS_AS_INT(8,0,AUDIO10_DATA_FORMAT_TYPE_I_PCM), \
 TUD_AUDIO10_DESC_TYPE_I_FORMAT(1,bytes,bits,rate), \
 TUD_AUDIO10_DESC_STD_AS_ISO_EP(USB_MIC_EP_IN,TUSB_XFER_ISOCHRONOUS|TUSB_ISO_EP_ATT_ASYNCHRONOUS,packet,1,0), \
 TUD_AUDIO10_DESC_CS_AS_ISO_EP(AUDIO10_CS_AS_ISO_DATA_EP_ATT_SAMPLING_FRQ,AUDIO10_CS_AS_ISO_DATA_EP_LOCK_DELAY_UNIT_UNDEFINED,0)
static const uint8_t configuration_template[]={
  TUD_CONFIG_DESCRIPTOR(1,4,0,TUD_CONFIG_DESC_LEN+AUDIO_CONTROL_LEN+USB_FORMAT_COUNT*AUDIO_ALT_LEN+USB_MIC_FORMAT_COUNT*MIC_ALT_LEN+TUD_HID_DESC_LEN,0,500),
  AUDIO_CONTROL_DESCRIPTORS,
  AUDIO_ALT_DESCRIPTORS(1,2,16,48000,196),
  AUDIO_ALT_DESCRIPTORS(2,3,24,96000,582),
  AUDIO_ALT_DESCRIPTORS(3,2,16,44100,184),
  AUDIO_ALT_DESCRIPTORS(4,3,24,44100,276),
  AUDIO_ALT_DESCRIPTORS(5,3,24,48000,294),
  AUDIO_ALT_DESCRIPTORS(6,2,16,88200,360),
  AUDIO_ALT_DESCRIPTORS(7,3,24,88200,540),
  AUDIO_ALT_DESCRIPTORS(8,2,16,96000,388),
  AUDIO_ALT_DESCRIPTORS(9,2,16,176400,712),
  AUDIO_ALT_DESCRIPTORS(10,2,16,192000,772),
  TUD_AUDIO10_DESC_STD_AS_INT(2,0,0,5),
  MIC_ALT(1,2,16,48000,98),MIC_ALT(2,2,16,16000,34),MIC_ALT(3,2,16,8000,18),
  MIC_ALT(4,2,16,32000,66),MIC_ALT(5,2,16,44100,92),MIC_ALT(6,2,16,96000,194),
  MIC_ALT(7,3,24,48000,147),MIC_ALT(8,3,24,44100,138),MIC_ALT(9,3,24,96000,291),
  TUD_HID_DESCRIPTOR(USB_HOST_CONTROL_ITF,0,HID_ITF_PROTOCOL_NONE,sizeof(profile_report_template),USB_HOST_CONTROL_EP_IN,2,10)
};
_Static_assert(sizeof(configuration_template)==TUD_CONFIG_DESC_LEN+AUDIO_CONTROL_LEN+USB_FORMAT_COUNT*AUDIO_ALT_LEN+USB_MIC_FORMAT_COUNT*MIC_ALT_LEN+TUD_HID_DESC_LEN,"UAC1 and HID template length");
static bool descriptor_ready;static uint32_t capture_mask=USB_MIC_DEFAULT_MASK,playback_mask=147;
uint8_t usb_descriptors_active_profile(void){return active_set.options.device_profile;}
bool usb_descriptors_active_hid_compat(void){return active_set.options.hid_consumer_compat;}
bool usb_descriptors_validate(const usb_descriptor_set_t *s){return usb_descriptor_set_validate(s);}
bool usb_descriptors_build(const usb_descriptor_options_t *o,usb_descriptor_set_t *s){return usb_descriptor_profile_build(o,s,configuration_template,sizeof(configuration_template));}
bool usb_descriptors_commit(const usb_descriptor_set_t *s){if(!usb_descriptors_validate(s))return false;active_set=*s;capture_mask=s->options.capture_formats_mask;playback_mask=s->options.formats_mask;descriptor_ready=true;return true;}
void usb_descriptors_set_capture_formats(uint32_t mask){if(usb_mic_mask_valid(mask)){capture_mask=mask;usb_descriptors_set_formats(playback_mask);}}
void usb_descriptors_set_formats(uint32_t mask){usb_descriptor_set_t s;usb_descriptor_options_t o={mask,capture_mask,active_set.options.device_profile,active_set.options.hid_consumer_compat};if(usb_descriptors_build(&o,&s))usb_descriptors_commit(&s);}
void usb_descriptors_set_hi_res_enabled(bool enabled){usb_descriptors_set_formats(usb_formats_legacy(enabled));}
static char names[3][USB_AUDIO_NAME_BYTES] = {"W5500-EVB-Pico2", "WIZnet", "W5500-EVB-Pico2"};
static uint16_t string_descriptor[64];

// Strict UTF-8 decoder; rejects overlongs, surrogate code points and controls.
static bool to_utf16(const char *text, uint16_t *out, uint8_t *count) {
  if (!text) return false;
  uint8_t n = 0;
  const unsigned char *p = (const unsigned char *)text;
  while (*p) {
    uint32_t c = *p++;
    unsigned follow = 0;
    uint32_t minimum = 0;
    if (c >= 0xc2 && c <= 0xdf) { follow=1; minimum=0x80; c &= 0x1f; }
    else if (c >= 0xe0 && c <= 0xef) { follow=2; minimum=0x800; c &= 0x0f; }
    else if (c >= 0xf0 && c <= 0xf4) { follow=3; minimum=0x10000; c &= 7; }
    else if (c >= 0x80) return false;
    for (unsigned i=0; i<follow; ++i) {
      if ((*p & 0xc0) != 0x80) return false;
      c = (c << 6) | (*p++ & 0x3f);
    }
    if (c < minimum || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff) ||
        c < 0x20 || (c >= 0x7f && c <= 0x9f)) return false;
    if (c > 0xffff) {
      if (n > 61) return false;
      c -= 0x10000;
      if (out) out[n] = (uint16_t)(0xd800 | (c >> 10));
      ++n;
      if (out) out[n] = (uint16_t)(0xdc00 | (c & 0x3ff));
      ++n;
    } else {
      if (n == 63) return false;
      if (out) out[n] = (uint16_t)c;
      ++n;
    }
  }
  if (!n || text[0] == ' ' || p[-1] == ' ') return false;
  *count = n;
  return true;
}
bool usb_descriptors_validate_name(const char *text) {
  uint8_t n;
  return to_utf16(text, NULL, &n);
}
bool usb_descriptors_set_names(const char *product, const char *manufacturer, const char *playback) {
  if (!usb_descriptors_validate_name(product) || !usb_descriptors_validate_name(manufacturer) ||
      !usb_descriptors_validate_name(playback)) return false;
  strcpy(names[0], product); strcpy(names[1], manufacturer); strcpy(names[2], playback);
  return true;
}
void usb_descriptors_get_names(char *product, char *manufacturer, char *playback) {
  if (product) strcpy(product, names[0]);
  if (manufacturer) strcpy(manufacturer, names[1]);
  if (playback) strcpy(playback, names[2]);
}
uint8_t const *tud_descriptor_device_cb(void) { return (uint8_t const *)&device_descriptor; }
uint8_t const *tud_descriptor_configuration_cb(uint8_t index) {
  return index == 0 && descriptor_ready ? active_set.configuration : NULL;
}
uint16_t const *tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
  (void)langid;
  uint8_t count = 0;
  if (index == 0) { string_descriptor[1] = 0x0409; count = 1; }
  else if (index == 3) {
    char serial[PICO_UNIQUE_BOARD_ID_SIZE_BYTES * 2 + 1];
    pico_get_unique_board_id_string(serial, sizeof(serial));
    if (!to_utf16(serial, string_descriptor + 1, &count)) return NULL;
  } else {
    const char *name = index == 1 ? names[1] : index == 2 ? names[0] : index == 4 ? names[2] : index == 5 ? "Microphone" : NULL;
    if (!to_utf16(name, string_descriptor + 1, &count)) return NULL;
  }
  string_descriptor[0] = (uint16_t)((TUSB_DESC_STRING << 8) | (2 + count*2));
  return string_descriptor;
}
