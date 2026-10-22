#define UAB_USB_CONTROL_NOTIFY 1
#include <assert.h>
#include <stdio.h>
#include "../usb/usb_control_notify.c"
static uint32_t now;static unsigned sent;static bool writable=true;
uint32_t time_us_32(void){return now;}
bool tud_audio_int_n_write(uint8_t func,const audio_interrupt_data_t *d){assert(func==0&&d->v1.bStatusType==0x80&&(d->v1.bOriginator==2||d->v1.bOriginator==7));if(!writable)return false;sent++;return true;}
int main(void){
 usb_control_notify_status_t s;usb_control_notify_mark(99);usb_control_notify_task();assert(sent==0);
 usb_control_notify_mark(2);usb_control_notify_mark(7);usb_control_notify_task();assert(sent==1&&usb_control_notify_pending(2));
 for(unsigned i=0;i<100;i++){now+=1000;usb_control_notify_task();}assert(sent==1);
 for(unsigned i=0;i<45;i++){now+=500001;usb_control_notify_task();}usb_control_notify_get_status(&s);assert(sent==40&&s.pending==3&&s.suppressed==2&&s.acknowledged==0);
 tud_audio_int_done_cb(0);assert(usb_control_notify_pending(2));usb_control_notify_ack(2);assert(!usb_control_notify_pending(2)&&usb_control_notify_pending(7));
 usb_control_notify_mark(2);now+=500001;usb_control_notify_task();assert(sent==41);usb_control_notify_reset();assert(!usb_control_notify_pending(2)&&!usb_control_notify_pending(7));
 now=0xfffffff0u;usb_control_notify_mark(2);usb_control_notify_task();assert(sent==42);now+=500001;usb_control_notify_task();assert(sent==43);
 writable=false;now+=500001;usb_control_notify_task();assert(sent==43&&usb_control_notify_pending(2));
 puts("PASS: production UAC1 notification merge, 500ms rate, per-entity retry cap, GET_STAT acknowledgement, reset and timer wrap (offline prototype)");return 0;
}
