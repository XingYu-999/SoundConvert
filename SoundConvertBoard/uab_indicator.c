#include <stdatomic.h>
#include "pico/stdlib.h"
#include "hardware/pwm.h"
#include "usb_audio.h"
#include "uab_indicator.h"

#define LED_GPIO 25u
#define LED_TOP 1000u
static atomic_uint configured_mode, identify_request;
static uint32_t updated_at, identify_started, identify_duration, activity_at;
static uint32_t previous_rx, previous_tx, previous_generation, previous_mic_generation;
static bool identifying, have_activity;

void uab_indicator_set_mode(uint8_t mode) {
    if (uab_indicator_mode_valid(mode)) atomic_store_explicit(&configured_mode,mode,memory_order_release);
}
void uab_indicator_identify(unsigned seconds) {
    if (seconds>=1 && seconds<=30) atomic_store_explicit(&identify_request,seconds,memory_order_release);
}
void uab_indicator_init(uint8_t mode) {
    identifying=have_activity=false;
    previous_rx=previous_tx=previous_generation=previous_mic_generation=0;
    updated_at=time_us_32()-10000u;
    atomic_store(&identify_request,0);
    uab_indicator_set_mode(uab_indicator_mode_valid(mode)?mode:UAB_INDICATOR_AUTO);
    // 150 MHz / 150 / 1000 is approximately 1 kHz; no PWM IRQ is needed.
    gpio_set_function(LED_GPIO,GPIO_FUNC_PWM);
    uint slice=pwm_gpio_to_slice_num(LED_GPIO);
    pwm_config config=pwm_get_default_config();
    pwm_config_set_clkdiv(&config,150.0f);
    pwm_config_set_wrap(&config,LED_TOP-1);
    pwm_init(slice,&config,false);
    pwm_set_gpio_level(LED_GPIO,0);
    pwm_set_enabled(slice,true);
}
static uint16_t breathe(uint32_t now_ms) {
    uint32_t phase=now_ms%2400u;
    uint32_t ramp=(phase<1200u?phase:2400u-phase)*LED_TOP/1200u;
    return (uint16_t)(ramp*ramp/LED_TOP);
}
void uab_indicator_task(void) {
    uint32_t now=time_us_32();
    if ((uint32_t)(now-updated_at)<10000u) return;
    updated_at=now;
    unsigned seconds=atomic_exchange_explicit(&identify_request,0,memory_order_acq_rel);
    if (seconds) { identifying=true; identify_started=now; identify_duration=seconds*1000000u; }
    if (identifying && (uint32_t)(now-identify_started)>=identify_duration) identifying=false;

    usb_audio_status_t usb; usb_audio_get_status(&usb);
    usb_mic_status_t mic; usb_microphone_get_status(&mic);
    bool connected=usb.state==USB_AUDIO_RUNNING && usb.mounted && !usb.suspended;
    bool playback=usb.alternate_setting!=0;
    bool capture=mic.active && mic.alternate_setting!=0;
    if (!connected || (!playback && !capture)) have_activity=false;
    else {
        // Generation changes discard activity from an old stream. A byte/frame
        // increase, including digital silence, establishes fresh activity.
        if (usb.generation!=previous_generation || mic.generation!=previous_mic_generation) have_activity=false;
        if ((playback && usb.rx_bytes!=previous_rx) || (capture && mic.tx_frames!=previous_tx &&
            (mic.generation==previous_mic_generation || mic.tx_frames!=0))) {
            have_activity=true; activity_at=now;
        }
    }
    previous_rx=usb.rx_bytes; previous_tx=mic.tx_frames;
    previous_generation=usb.generation; previous_mic_generation=mic.generation;
    bool active=have_activity && (uint32_t)(now-activity_at)<200000u;
    uint32_t ms=(uint32_t)(time_us_64()/1000u);
    unsigned mode=atomic_load_explicit(&configured_mode,memory_order_acquire);
    uint16_t level=0;
    if (identifying) level=((uint32_t)(now-identify_started)/100000u)%2u?0:LED_TOP;
    else if (mode==UAB_INDICATOR_ON) level=LED_TOP;
    else if (mode==UAB_INDICATOR_BREATHE) level=breathe(ms);
    else if (mode==UAB_INDICATOR_BLINK) level=ms%1000u<200u?LED_TOP:0;
    else if (mode==UAB_INDICATOR_AUTO && connected)
        level=active?(ms%320u<100u?LED_TOP:0):breathe(ms);
    pwm_set_gpio_level(LED_GPIO,level);
}
