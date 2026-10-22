#include <stdatomic.h>
#include <string.h>
#include "pico/stdlib.h"
#include "tusb.h"
#include "usb_host_control.h"
#define KEY_QUEUE_SIZE 8u
static uint16_t keys[KEY_QUEUE_SIZE];
static unsigned key_epochs[KEY_QUEUE_SIZE];
static atomic_uint epoch;
static const struct { const char *name; uint16_t usage; } supported_keys[]={
 {"play_pause",USB_HOST_PLAY_PAUSE},{"next_track",USB_HOST_NEXT_TRACK},
 {"previous_track",USB_HOST_PREVIOUS_TRACK},{"volume_up",USB_HOST_VOLUME_UP},
 {"volume_down",USB_HOST_VOLUME_DOWN},{"mute",USB_HOST_MUTE}
};
const char *usb_host_control_key_name(unsigned index){return index<sizeof(supported_keys)/sizeof(supported_keys[0])?supported_keys[index].name:NULL;}
uint16_t usb_host_control_key_usage(const char *key){
 if(key)for(unsigned i=0;i<sizeof(supported_keys)/sizeof(supported_keys[0]);i++)if(!strcmp(key,supported_keys[i].name))return supported_keys[i].usage;
 return 0;
}
static uint16_t key_report(uint16_t usage){
 for(unsigned i=0;i<sizeof(supported_keys)/sizeof(supported_keys[0]);i++)if(supported_keys[i].usage==usage)return (uint16_t)(1u<<i);
 return 0;
}
static atomic_uint write_at,read_at,queued,completed,dropped,last_usage,phase;
static atomic_bool ready;
static uint32_t release_at;
static bool attached;
// phase: 0 idle, 1 press in flight, 2 held, 3 release in flight,
// 4 initial release required, 5 initial release in flight.
bool usb_host_control_enqueue(uint16_t usage){
 bool valid=false;for(unsigned i=0;i<sizeof(supported_keys)/sizeof(supported_keys[0]);i++)if(usage==supported_keys[i].usage)valid=true;
 if(!valid)return false;
 unsigned generation=atomic_load_explicit(&epoch,memory_order_acquire);
 if(!atomic_load_explicit(&ready,memory_order_acquire))return false;
 unsigned wr=atomic_load_explicit(&write_at,memory_order_relaxed),rd=atomic_load_explicit(&read_at,memory_order_acquire);
 if(wr-rd>=KEY_QUEUE_SIZE)return false;
 keys[wr%KEY_QUEUE_SIZE]=usage;key_epochs[wr%KEY_QUEUE_SIZE]=generation;atomic_store_explicit(&write_at,wr+1,memory_order_release);atomic_fetch_add(&queued,1);return true;
}
void usb_host_control_get_status(usb_host_control_status_t*s){
 s->ready=atomic_load(&ready);s->queued=atomic_load(&queued);s->completed=atomic_load(&completed);s->dropped=atomic_load(&dropped);s->last_usage=(uint16_t)atomic_load(&last_usage);
 unsigned rd=atomic_load(&read_at),wr=atomic_load(&write_at),p=atomic_load(&phase);unsigned count=wr-rd;s->pending=(count>KEY_QUEUE_SIZE?0:count)+(p>=1&&p<=3?1:0);
}
void usb_host_control_task(bool usable){
 if(!usable){
  atomic_store_explicit(&ready,false,memory_order_release);
  if(attached)atomic_fetch_add_explicit(&epoch,1,memory_order_acq_rel);
  unsigned wr=atomic_load_explicit(&write_at,memory_order_acquire),rd=atomic_exchange(&read_at,wr),p=atomic_exchange(&phase,4);
  atomic_fetch_add(&dropped,wr-rd+(p>=1&&p<=3?1:0));attached=false;return;
 }
 if(!attached){attached=true;atomic_store(&phase,4);}
 if(!tud_hid_ready())return;
 unsigned p=atomic_load(&phase);uint16_t report=0;
 if(p==4){atomic_store(&phase,5);if(!tud_hid_report(0,&report,2))atomic_store(&phase,4);return;}
 if(p==2&&(int32_t)(time_us_32()-release_at)>=0){atomic_store(&phase,3);if(!tud_hid_report(0,&report,2))atomic_store(&phase,2);return;}
 if(p!=0)return;
 atomic_store_explicit(&ready,true,memory_order_release);
 unsigned rd=atomic_load_explicit(&read_at,memory_order_relaxed),wr=atomic_load_explicit(&write_at,memory_order_acquire);
 if(rd==wr)return;
 // An enqueue racing a detach must never become a delayed action on reconnect.
 if(key_epochs[rd%KEY_QUEUE_SIZE]!=atomic_load_explicit(&epoch,memory_order_acquire)){
  atomic_store_explicit(&read_at,rd+1,memory_order_release);atomic_fetch_add(&dropped,1);return;
 }
 uint16_t usage=keys[rd%KEY_QUEUE_SIZE];report=key_report(usage);atomic_store(&phase,1);
 if(tud_hid_report(0,&report,2)){atomic_store(&last_usage,usage);atomic_store_explicit(&read_at,rd+1,memory_order_release);}
 else atomic_store(&phase,0);
}
void tud_hid_report_complete_cb(uint8_t instance,uint8_t const*report,uint16_t len){
 (void)instance;(void)report;if(len!=2)return;
 switch(atomic_load(&phase)){
  case 1:release_at=time_us_32()+10000;atomic_store(&phase,2);break;
  case 3:atomic_fetch_add(&completed,1);atomic_store(&phase,0);break;
  case 5:atomic_store(&phase,0);break;
 }
}
uint16_t tud_hid_get_report_cb(uint8_t instance,uint8_t report_id,hid_report_type_t type,uint8_t*buffer,uint16_t length){
 (void)instance;(void)report_id;if(type!=HID_REPORT_TYPE_INPUT||length<2)return 0;buffer[0]=buffer[1]=0;return 2;
}
void tud_hid_set_report_cb(uint8_t instance,uint8_t report_id,hid_report_type_t type,uint8_t const*buffer,uint16_t length){
 (void)instance;(void)report_id;(void)type;(void)buffer;(void)length;
}
