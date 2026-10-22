#include "usb_control_notify.h"
#include <stdatomic.h>
#if UAB_USB_CONTROL_NOTIFY
#include "pico/stdlib.h"
#include "tusb.h"
static atomic_uint pending,marked,scheduled,completed,acknowledged,suppressed;
static unsigned attempts[2],cursor;static uint32_t last_attempt;static bool attempted;
static unsigned entity_bit(uint8_t entity){return entity==2?1:entity==7?2:0;}
void usb_control_notify_mark(uint8_t entity){unsigned bit=entity_bit(entity);if(bit){atomic_fetch_or(&pending,bit);atomic_fetch_add(&marked,1);}}
bool usb_control_notify_pending(uint8_t entity){return (atomic_load(&pending)&entity_bit(entity))!=0;}
void usb_control_notify_ack(uint8_t entity){unsigned bit=entity_bit(entity);if(bit){atomic_fetch_and(&pending,~bit);attempts[entity==7]=0;atomic_fetch_add(&acknowledged,1);}}
void usb_control_notify_reset(void){atomic_store(&pending,0);attempts[0]=attempts[1]=0;attempted=false;}
void usb_control_notify_task(void){
 uint32_t now=time_us_32();if(attempted&&now-last_attempt<500000)return;
 unsigned bits=atomic_load(&pending);if(!bits)return;
 for(unsigned n=0;n<2;n++){unsigned index=(cursor+n)%2;if(!(bits&(1u<<index))||attempts[index]>=20)continue;
  last_attempt=now;attempted=true;audio_interrupt_data_t data={0};data.v1.bStatusType=0x80;data.v1.bOriginator=index?7:2;
  if(tud_audio_int_n_write(0,&data)){atomic_fetch_add(&scheduled,1);if(++attempts[index]==20)atomic_fetch_add(&suppressed,1);cursor=1-index;}return;
 }
}
void tud_audio_int_done_cb(uint8_t rhport){(void)rhport;atomic_fetch_add(&completed,1);}
void usb_control_notify_get_status(usb_control_notify_status_t *s){*s=(usb_control_notify_status_t){true,atomic_load(&pending),atomic_load(&marked),atomic_load(&scheduled),atomic_load(&completed),atomic_load(&acknowledged),atomic_load(&suppressed)};}
#else
void usb_control_notify_mark(uint8_t entity){(void)entity;}void usb_control_notify_task(void){}void usb_control_notify_ack(uint8_t entity){(void)entity;}void usb_control_notify_reset(void){}
bool usb_control_notify_pending(uint8_t entity){(void)entity;return false;}
void usb_control_notify_get_status(usb_control_notify_status_t *s){*s=(usb_control_notify_status_t){0};}
#endif
