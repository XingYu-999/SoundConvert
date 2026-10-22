#pragma once
#include <stdbool.h>
#include "uab_config_store.h"

// Core 0 only, once before Core 1 launch. Loads names before the first attach,
// then consumes this boot's automatic START without consulting the network.
bool uab_boot_initialize(uab_config_t *config);
bool uab_boot_auto_start_pending(void);
