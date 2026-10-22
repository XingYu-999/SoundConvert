#include <stdatomic.h>
#include <string.h>
#include "uab_boot.h"
#include "usb_audio.h"

static atomic_bool start_pending = true;
static bool boot_initialized;

bool uab_boot_auto_start_pending(void) {
    return atomic_load_explicit(&start_pending, memory_order_acquire);
}

bool uab_boot_initialize(uab_config_t *config) {
    // A repeated call cannot turn a deliberate STOP into an implicit START.
    if (boot_initialized) return false;
    boot_initialized = true;
    usb_audio_init();
    memset(config, 0, sizeof(*config));
    usb_audio_get_names(config->product, config->manufacturer, config->playback);
    config->revision = 1;config->formats_mask=147;config->hi_res_enabled=true;config->capture_formats_mask=USB_MIC_DEFAULT_MASK;
    config->mask[0] = config->mask[1] = config->mask[2] = 255;
    uab_config_load(config);

    usb_descriptor_options_t options={config->formats_mask?config->formats_mask:usb_formats_legacy(config->hi_res_enabled),config->capture_formats_mask?config->capture_formats_mask:USB_MIC_DEFAULT_MASK,config->device_profile,config->hid_consumer_compat};
    bool valid = usb_audio_set_names(config->product, config->manufacturer, config->playback) && usb_audio_apply_descriptor_options(&options);
    if (valid) {
        usb_audio_set_enabled(true);
        // Consume START on the USB owner before starting any W5500 work.
        usb_audio_task();
    }
    atomic_store_explicit(&start_pending, false, memory_order_release);
    usb_audio_status_t status;
    usb_audio_get_status(&status);
    return valid && status.state == USB_AUDIO_RUNNING;
}
