#pragma once
#include <stdbool.h>
#include <stdint.h>
#ifndef UAB_USB_CONTROL_NOTIFY
#define UAB_USB_CONTROL_NOTIFY 0
#endif
typedef struct {bool enabled;uint32_t pending,marked,scheduled,completed,acknowledged,suppressed;}usb_control_notify_status_t;
void usb_control_notify_mark(uint8_t entity_id);
void usb_control_notify_task(void);
void usb_control_notify_ack(uint8_t entity_id);
void usb_control_notify_reset(void);
void usb_control_notify_get_status(usb_control_notify_status_t *out);
bool usb_control_notify_pending(uint8_t entity_id);
