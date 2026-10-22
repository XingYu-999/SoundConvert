#ifndef UAB_USB_HOST_CONTROL_H
#define UAB_USB_HOST_CONTROL_H
#include <stdbool.h>
#include <stdint.h>
#define USB_HOST_VOLUME_UP 0x00e9u
#define USB_HOST_VOLUME_DOWN 0x00eau
#define USB_HOST_MUTE 0x00e2u
#define USB_HOST_PLAY_PAUSE 0x00cdu
#define USB_HOST_NEXT_TRACK 0x00b5u
#define USB_HOST_PREVIOUS_TRACK 0x00b6u
// A single whitelist is shared by protocol validation and capability reporting.
uint16_t usb_host_control_key_usage(const char *key);
const char *usb_host_control_key_name(unsigned index);
typedef struct { bool ready; uint32_t queued, completed, dropped, pending; uint16_t last_usage; } usb_host_control_status_t;
// Core 1 producer; each accepted entry represents one press followed by release.
bool usb_host_control_enqueue(uint16_t usage);
void usb_host_control_get_status(usb_host_control_status_t *status);
// Core 0 only, after TinyUSB dispatch. No network, allocation, logging or Flash.
void usb_host_control_task(bool usable);
#endif
