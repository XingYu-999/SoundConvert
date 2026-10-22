// Production controller with a fake USB snapshot, clock and PWM register sink.
#include <assert.h>
#include <stdio.h>
#include "../uab_indicator.c"
static uint64_t fake_us;
static usb_audio_status_t fake_usb;
static usb_mic_status_t fake_mic;
static unsigned writes, checks;
static uint16_t brightness;
#define CHECK(x) do { ++checks; assert(x); } while(0)
uint32_t time_us_32(void) { return (uint32_t)fake_us; }
uint64_t time_us_64(void) { return fake_us; }
void usb_audio_get_status(usb_audio_status_t *out) { *out=fake_usb; }
void usb_microphone_get_status(usb_mic_status_t *out) { *out=fake_mic; }
void gpio_set_function(uint gpio, int function) { CHECK(gpio==25 && function==GPIO_FUNC_PWM); }
uint pwm_gpio_to_slice_num(uint gpio) { CHECK(gpio==25); return 4; }
pwm_config pwm_get_default_config(void) { return (pwm_config){0}; }
void pwm_config_set_clkdiv(pwm_config *config, float divider) { config->divider=divider; }
void pwm_config_set_wrap(pwm_config *config, uint16_t top) { config->top=top; }
void pwm_init(uint slice, const pwm_config *config, bool enabled) { CHECK(slice==4 && !enabled && config->divider==150 && config->top==999); }
void pwm_set_gpio_level(uint gpio, uint16_t level) { CHECK(gpio==25 && level<=1000); brightness=level; ++writes; }
void pwm_set_enabled(uint slice, bool enabled) { CHECK(slice==4 && enabled); }
static void at(uint64_t ms) { fake_us=ms*1000; uab_indicator_task(); }
int main(void) {
    uab_indicator_init(UAB_INDICATOR_AUTO); at(10); CHECK(brightness==0);
    fake_usb.state=USB_AUDIO_RUNNING;fake_usb.mounted=true;
    at(600); CHECK(brightness==250); at(1200); CHECK(brightness==1000); at(1800); CHECK(brightness==250);
    fake_usb.alternate_setting=1;fake_usb.rx_bytes=384;at(1940);CHECK(brightness==1000);
    at(2050);CHECK(brightness==0); // Grace preserves blinking through short inter-packet gaps.
    at(2160);CHECK(brightness==40); // No fresh bytes for 200 ms returns to breathing.
    at(2260);CHECK(brightness<1000); // Selected alt/cumulative bytes alone cannot sustain activity.
    fake_usb.alternate_setting=0;fake_mic.active=true;fake_mic.alternate_setting=1;fake_mic.tx_frames=48;
    at(2580);CHECK(brightness==1000); // Microphone-only traffic also indicates actual USB activity.
    fake_mic.generation++;fake_mic.tx_frames=0;at(2660);CHECK(brightness>0 && brightness<1000);
    fake_usb.suspended=true;at(2700);CHECK(brightness==0);
    fake_usb.suspended=false;fake_usb.mounted=false;at(2740);CHECK(brightness==0);
    fake_usb.mounted=true;fake_usb.state=USB_AUDIO_STOPPED;at(2780);CHECK(brightness==0);
    uab_indicator_set_mode(UAB_INDICATOR_ON);at(2800);CHECK(brightness==1000);
    unsigned before=writes;fake_us+=100;uab_indicator_task();CHECK(writes==before); // Bounded update rate.
    uab_indicator_set_mode(UAB_INDICATOR_OFF);at(2820);CHECK(brightness==0);
    uab_indicator_identify(1);at(2840);CHECK(brightness==1000);at(2940);CHECK(brightness==0);
    uab_indicator_set_mode(UAB_INDICATOR_ON);at(3040);CHECK(brightness==1000);
    at(3940);CHECK(brightness==1000); // Restore latest selected mode after identify expires.
    uab_indicator_set_mode(UAB_INDICATOR_BREATHE);at(4200);CHECK(brightness==250);
    uab_indicator_set_mode(UAB_INDICATOR_BLINK);at(5000);CHECK(brightness==1000);at(5300);CHECK(brightness==0);
    uab_indicator_identify(0);uab_indicator_identify(31);at(5400);CHECK(brightness==0);
    uab_indicator_set_mode(UAB_INDICATOR_OFF);at(5500);uab_indicator_identify(1);at(5520);at(6520);CHECK(brightness==0);
    // The 32-bit microsecond timer wraps every ~71 minutes. Overrides must expire across it.
    fake_us=UINT32_MAX-50000u;uab_indicator_identify(1);uab_indicator_task();CHECK(brightness==1000);
    fake_us+=1100000u;uab_indicator_task();CHECK(brightness==0);
    uint8_t mode=99;CHECK(!uab_indicator_mode_parse("unknown",&mode) && mode==99);
    for(unsigned i=0;i<UAB_INDICATOR_MODE_COUNT;++i)CHECK(uab_indicator_mode_parse(uab_indicator_mode_name(i),&mode) && mode==i);
    printf("PASS: %u production LD2 USB activity, PWM, identify, mode and timer-wrap checks\n",checks);
    return 0;
}
