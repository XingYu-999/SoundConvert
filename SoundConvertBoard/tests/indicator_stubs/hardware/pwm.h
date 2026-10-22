#pragma once
#include <stdbool.h>
#include <stdint.h>
typedef unsigned uint;
typedef struct { float divider; uint16_t top; } pwm_config;
#define GPIO_FUNC_PWM 4
void gpio_set_function(uint gpio, int function);
uint pwm_gpio_to_slice_num(uint gpio);
pwm_config pwm_get_default_config(void);
void pwm_config_set_clkdiv(pwm_config *config, float divider);
void pwm_config_set_wrap(pwm_config *config, uint16_t top);
void pwm_init(uint slice, const pwm_config *config, bool enabled);
void pwm_set_gpio_level(uint gpio, uint16_t level);
void pwm_set_enabled(uint slice, bool enabled);
