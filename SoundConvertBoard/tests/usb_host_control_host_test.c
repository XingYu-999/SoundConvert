// Exercise the production Core-0 HID state machine with controlled transfer completion.
// No claim about multicore scheduling, physical enumeration or player response.
#include <assert.h>
#include <stdio.h>
#include "../usb/usb_host_control.c"
static unsigned checks,count;
static uint32_t now;
static uint16_t reports[256];
static bool endpoint_ready=true,send_ok=true;
#define CHECK(x) do{checks++;assert(x);}while(0)
uint32_t time_us_32(void){return now;}
bool tud_hid_ready(void){return endpoint_ready;}
bool tud_hid_report(uint8_t id,const void*report,uint16_t length){CHECK(id==0&&length==2);if(!send_ok)return false;assert(count<256);memcpy(&reports[count++],report,2);return true;}
static void complete(void){uint8_t report[2]={0};tud_hid_report_complete_cb(0,report,2);}
static void connect_host(void){unsigned before=count;usb_host_control_task(true);CHECK(count==before+1&&reports[before]==0);complete();usb_host_control_task(true);usb_host_control_status_t s;usb_host_control_get_status(&s);CHECK(s.ready&&s.pending==0);}
static void tap(uint16_t usage){unsigned before=count;usb_host_control_task(true);CHECK(count==before+1&&reports[before]==key_report(usage));complete();now+=9999;usb_host_control_task(true);CHECK(count==before+1);now++;usb_host_control_task(true);CHECK(count==before+2&&reports[before+1]==0);complete();}
int main(void){
 const char*names[]={"play_pause","next_track","previous_track","volume_up","volume_down","mute"};
 uint16_t usages[]={0xcd,0xb5,0xb6,0xe9,0xea,0xe2};
 CHECK(!usb_host_control_enqueue(0xcd));CHECK(usb_host_control_key_usage(NULL)==0);CHECK(usb_host_control_key_usage("play")==0);CHECK(usb_host_control_key_usage("PLAY_PAUSE")==0);
 for(unsigned i=0;i<6;i++){CHECK(!strcmp(usb_host_control_key_name(i),names[i]));CHECK(usb_host_control_key_usage(names[i])==usages[i]);CHECK(key_report(usages[i])==(1u<<i));}CHECK(usb_host_control_key_name(6)==NULL);
 connect_host();CHECK(!usb_host_control_enqueue(0));CHECK(!usb_host_control_enqueue(0xb7));
 for(unsigned i=0;i<8;i++)CHECK(usb_host_control_enqueue(usages[i%6]));CHECK(!usb_host_control_enqueue(0xcd));
 for(unsigned i=0;i<8;i++)tap(usages[i%6]);usb_host_control_status_t s;usb_host_control_get_status(&s);CHECK(s.completed==8&&s.pending==0&&s.queued==8);
 // Busy interrupt IN retains discrete queued actions in order.
 CHECK(usb_host_control_enqueue(0xcd));endpoint_ready=false;unsigned before=count;usb_host_control_task(true);CHECK(count==before);endpoint_ready=true;tap(0xcd);
 // Failed press and release submissions are retried by Core 0, never duplicated as taps.
 CHECK(usb_host_control_enqueue(0xb5));send_ok=false;usb_host_control_task(true);CHECK(count==before+2);send_ok=true;usb_host_control_task(true);CHECK(reports[count-1]==2);complete();now+=10000;send_ok=false;usb_host_control_task(true);send_ok=true;usb_host_control_task(true);CHECK(reports[count-1]==0);complete();
 // Detach while held, with additional queued actions; reconnect begins with neutral.
 CHECK(usb_host_control_enqueue(0xcd));CHECK(usb_host_control_enqueue(0xb6));usb_host_control_task(true);complete();usb_host_control_task(false);usb_host_control_get_status(&s);CHECK(!s.ready&&s.pending==0&&s.dropped==2);CHECK(!usb_host_control_enqueue(0xea));complete();connect_host();before=count;usb_host_control_task(true);CHECK(count==before);
 // Suspend uses the same invalidation path and never replays a queued toggle.
 CHECK(usb_host_control_enqueue(0xcd));usb_host_control_task(false);connect_host();before=count;usb_host_control_task(true);CHECK(count==before);usb_host_control_get_status(&s);CHECK(s.dropped==3);
 // Release deadline remains correct across the microsecond counter wrap.
 now=UINT32_MAX-5000;CHECK(usb_host_control_enqueue(0xe2));tap(0xe2);usb_host_control_get_status(&s);CHECK(s.completed==11&&s.pending==0);
 printf("PASS: %u HID production-state checks; six usages, bounded FIFO, neutral/press/release, transfer failure, detach, suspend and timer wrap. Offline only.\n",checks);return 0;
}
