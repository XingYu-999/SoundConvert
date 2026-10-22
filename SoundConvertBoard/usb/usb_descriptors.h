#ifndef SOUND_CONVERT_USB_DESCRIPTORS_H
#define SOUND_CONVERT_USB_DESCRIPTORS_H
#include <stdbool.h>
#include <stdint.h>
#include "usb_device_profile.h"
#define USB_DESCRIPTOR_CAPACITY 1600
typedef struct {uint32_t formats_mask,capture_formats_mask;uint8_t device_profile;bool hid_consumer_compat;} usb_descriptor_options_t;
typedef struct {uint8_t configuration[USB_DESCRIPTOR_CAPACITY],report[64];uint16_t length,report_length;usb_descriptor_options_t options;} usb_descriptor_set_t;
bool usb_descriptors_build(const usb_descriptor_options_t *options,usb_descriptor_set_t *staging);
bool usb_descriptors_validate(const usb_descriptor_set_t *set);
bool usb_descriptors_commit(const usb_descriptor_set_t *staging);
uint8_t usb_descriptors_active_profile(void);
bool usb_descriptors_active_hid_compat(void);
#define USB_AUDIO_EP_OUT 0x01
#define USB_AUDIO_EP_FB 0x81
#define USB_AUDIO_CONTROL_ITF 0
#define USB_AUDIO_STREAMING_ITF 1
#define USB_AUDIO_FEATURE_UNIT 2
#define USB_MIC_FEATURE_UNIT 7
#define USB_MIC_EP_IN 0x82
#define USB_MIC_STREAMING_ITF 2
#define USB_HOST_CONTROL_ITF 3
#define USB_HOST_CONTROL_EP_IN 0x83
void usb_descriptors_set_capture_formats(uint32_t mask);
bool usb_descriptors_validate_name(const char *text);
bool usb_descriptors_set_names(const char *product, const char *manufacturer, const char *playback);
void usb_descriptors_get_names(char *product, char *manufacturer, char *playback);
void usb_descriptors_set_hi_res_enabled(bool enabled);
void usb_descriptors_set_formats(uint32_t mask);
#endif
