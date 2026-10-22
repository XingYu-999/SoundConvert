#ifndef UAB_INDICATOR_H
#define UAB_INDICATOR_H
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

enum { UAB_INDICATOR_AUTO, UAB_INDICATOR_OFF, UAB_INDICATOR_ON,
       UAB_INDICATOR_BREATHE, UAB_INDICATOR_BLINK, UAB_INDICATOR_MODE_COUNT };
static inline bool uab_indicator_mode_valid(unsigned mode) { return mode < UAB_INDICATOR_MODE_COUNT; }
static inline const char *uab_indicator_mode_name(unsigned mode) {
    static const char *const names[] = {"auto", "off", "on", "breathe", "blink"};
    return uab_indicator_mode_valid(mode) ? names[mode] : "unknown";
}
static inline bool uab_indicator_mode_parse(const char *name, uint8_t *mode) {
    if (!name || !mode) return false;
    for (unsigned i=0; i<UAB_INDICATOR_MODE_COUNT; ++i)
        if (!strcmp(name,uab_indicator_mode_name(i))) { *mode=(uint8_t)i; return true; }
    return false;
}
// Core 0 owns PWM and samples USB state. Core 1 only posts atomic requests.
void uab_indicator_init(uint8_t mode);
void uab_indicator_set_mode(uint8_t mode);
void uab_indicator_identify(unsigned seconds);
void uab_indicator_task(void);
#endif
