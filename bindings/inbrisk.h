#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Each call connects to the running inbrisk.exe, then disconnects.
   Returns the number of JSON bytes written into buf, or -1.
   buf is not NUL-terminated. */

intptr_t inbrisk_status(uint8_t *buf, size_t cap);

/* plan_json is a NUL-terminated plan object:
   {"steps":[...]}, {"op":"run","steps":[...]} or {"plan":{"steps":[...]}}.
   dry_run != 0 validates the plan and does not launch or type. */
intptr_t inbrisk_run_json(const char *plan_json, int dry_run, uint8_t *buf, size_t cap);

#ifdef __cplusplus
}
#endif
