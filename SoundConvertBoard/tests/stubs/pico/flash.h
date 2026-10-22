#include <stdint.h>
#define PICO_OK 0
#define __not_in_flash_func(name) name
int flash_safe_execute(void (*callback)(void *), void *arg, uint32_t timeout);
