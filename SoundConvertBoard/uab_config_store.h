#ifndef UAB_CONFIG_STORE_H
#define UAB_CONFIG_STORE_H
#include <stdbool.h>
#include <stdint.h>
#include "usb_device_profile.h"
#include "uab_indicator.h"
typedef struct {
  uint64_t revision;
  char product[253], manufacturer[253], playback[253];
  uint8_t mode; // 0 DHCP, 1 static
  uint8_t ip[4], mask[4], gateway[4], dns[4];
  bool hi_res_enabled; // false keeps the legacy 16-bit / 48 kHz descriptor profile
  uint32_t formats_mask; // zero in legacy callers means derive from hi_res_enabled
  uint32_t capture_formats_mask; // zero migrates to 48k/16 mono
  uint8_t indicator_mode; // LD2 auto/off/on/breathe/blink; legacy defaults to auto
  uint8_t device_profile;
  bool hid_consumer_compat; // Explicit Windows compatibility; false retains accessory collection by profile.
} uab_config_t;
// Core 0 loads the boot snapshot before Core 1 launch. Core 1 owns subsequent
// transactions and prohibits START during save. load leaves *config unchanged
// if no committed valid slot exists.
bool uab_config_load(uab_config_t *config);
bool uab_config_save(const uab_config_t *config);
#endif
