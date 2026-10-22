#include <stdbool.h>
#include <stdint.h>
typedef union {struct{uint8_t bStatusType,bOriginator;}v1;}audio_interrupt_data_t;
bool tud_audio_int_n_write(uint8_t func,const audio_interrupt_data_t *data);
typedef struct { uint8_t bmRequestType, bRequest; uint16_t wValue, wIndex, wLength; } tusb_control_request_t;
typedef struct { int role, speed; } tusb_rhport_init_t;
typedef struct { int method; uint32_t sample_freq; struct { uint16_t fifo_threshold; } fifo_count; } audio_feedback_params_t;
typedef struct {uint32_t value_q16;uint16_t fifo_average_bytes,fifo_threshold_bytes;uint8_t method;} audio_feedback_state_t;
bool tud_audio_n_get_feedback_state(uint8_t func_id,audio_feedback_state_t *state);
enum { TUSB_ROLE_DEVICE=1, TUSB_SPEED_FULL=1, AUDIO_FEEDBACK_METHOD_FIFO_COUNT=4,
       AUDIO10_EP_CTRL_SAMPLING_FREQ=1, AUDIO10_CS_REQ_SET_CUR=1,
       AUDIO10_CS_REQ_GET_CUR=0x81, AUDIO10_CS_REQ_GET_MIN=0x82,
       AUDIO10_CS_REQ_GET_MAX=0x83, AUDIO10_CS_REQ_GET_RES=0x84,
       AUDIO10_FU_CTRL_MUTE=1, AUDIO10_FU_CTRL_VOLUME=2 };
bool tusb_init(uint8_t port, const tusb_rhport_init_t *init);
bool tud_connect(void);
bool tud_disconnect(void);
void tud_task_ext(uint32_t timeout, bool in_isr);
bool tud_task_event_ready(void);
bool tud_audio_clear_ep_out_ff(void);
bool tud_audio_clear_ep_in_ff(void);
typedef enum { HID_REPORT_TYPE_INPUT=1,HID_REPORT_TYPE_OUTPUT=2,HID_REPORT_TYPE_FEATURE=3 } hid_report_type_t;
bool tud_hid_ready(void);
bool tud_hid_report(uint8_t report_id,const void *report,uint16_t length);
uint16_t tud_audio_available(void);
uint16_t tud_audio_read(void *buffer, uint16_t size);
bool tud_audio_buffer_and_schedule_control_xfer(uint8_t port, const tusb_control_request_t *r, void *data, uint16_t size);
#ifdef UAB_MICROPHONE_FIFO_TEST
typedef struct{uint16_t count,depth;uint8_t data[16384];}tu_fifo_t;
uint16_t tu_fifo_count(tu_fifo_t*ff);
uint16_t tu_fifo_remaining(tu_fifo_t*ff);
tu_fifo_t*tud_audio_get_ep_in_ff(void);
uint16_t tud_audio_write(const void*data,uint16_t bytes);
bool tud_audio_clear_ep_in_ff(void);
void tud_audio_n_set_ep_in_fifo_threshold(uint8_t func,uint16_t threshold);
#endif
