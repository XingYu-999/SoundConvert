// Runs production startup plus USB owner with no network code linked.
// Physical enumeration, IRQ timing and electrical attach remain board tests.
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../usb/usb_audio.c"
#include "stubs/microphone_stub.h"
#include "../uab_boot.c"
static uint32_t fake_time=100;
static unsigned checks,loads,attaches,reconnects;
static bool saved=true,names_valid=true,attach_ok=true;
static bool saved_hi_res,descriptor_hi_res;
static char descriptor_names[3][253]={"W5500-EVB-Pico2","WIZnet","W5500-EVB-Pico2"};
#define CHECK(x) do { ++checks; assert(x); } while(0)
uint32_t time_us_32(void){return fake_time;}
uint64_t time_us_64(void){return fake_time;}
bool uab_config_load(uab_config_t*c){
    CHECK(current.state==USB_AUDIO_STOPPED && attaches==0);
    CHECK(uab_boot_auto_start_pending());++loads;
    if(!saved)return false;
    strcpy(c->product,u8"声音桥接验收");strcpy(c->manufacturer,"Saved manufacturer");strcpy(c->playback,u8"已保存播放接口");c->revision=42;c->hi_res_enabled=saved_hi_res;c->formats_mask=usb_formats_legacy(saved_hi_res);return true;
}
bool usb_descriptors_validate_name(const char*s){return s&&*s;}
bool usb_descriptors_set_names(const char*a,const char*b,const char*c){
    if(!names_valid)return false;
    strcpy(descriptor_names[0],a);strcpy(descriptor_names[1],b);strcpy(descriptor_names[2],c);return true;
}
void usb_descriptors_set_hi_res_enabled(bool enabled){descriptor_hi_res=enabled;}
void usb_descriptors_set_formats(uint32_t mask){descriptor_hi_res=(mask&~1u)!=0;}
void usb_descriptors_get_names(char*a,char*b,char*c){strcpy(a,descriptor_names[0]);strcpy(b,descriptor_names[1]);strcpy(c,descriptor_names[2]);}
bool tusb_init(uint8_t port,const tusb_rhport_init_t*init){
    (void)port;CHECK(init->role==TUSB_ROLE_DEVICE&&init->speed==TUSB_SPEED_FULL);
    CHECK(loads==1);CHECK(!strcmp(descriptor_names[0],saved?u8"声音桥接验收":"W5500-EVB-Pico2"));
    CHECK(!strcmp(descriptor_names[2],saved?u8"已保存播放接口":"W5500-EVB-Pico2"));
    CHECK(current.hi_res_enabled==(saved?saved_hi_res:true)&&descriptor_hi_res==(saved?saved_hi_res:true));
    ++attaches;return attach_ok;
}
bool tud_connect(void){++reconnects;return true;}bool tud_disconnect(void){return true;}
void tud_task_ext(uint32_t timeout,bool isr){(void)timeout;(void)isr;}
bool tud_task_event_ready(void){return false;}bool tud_audio_clear_ep_out_ff(void){return true;}
uint16_t tud_audio_available(void){return 0;}uint16_t tud_audio_read(void*p,uint16_t n){(void)p;(void)n;return 0;}
bool tud_audio_n_get_feedback_state(uint8_t id,audio_feedback_state_t*state){(void)id;(void)state;return false;}
bool tud_audio_buffer_and_schedule_control_xfer(uint8_t p,const tusb_control_request_t*r,void*d,uint16_t n){(void)p;(void)r;(void)d;(void)n;return false;}
static void step(unsigned us){fake_time+=us;usb_audio_task();}
int main(int argc,char**argv){
    if(argc>1){saved=strcmp(argv[1],"--defaults")!=0;names_valid=strcmp(argv[1],"--bad-names")!=0;attach_ok=strcmp(argv[1],"--init-failure")!=0;saved_hi_res=!strcmp(argv[1],"--hires");}
    uab_config_t config;CHECK(uab_boot_auto_start_pending());
    bool result=uab_boot_initialize(&config);CHECK(result==(names_valid&&attach_ok));
    CHECK(!uab_boot_auto_start_pending()&&loads==1);CHECK(config.revision==(saved?42:1));
    CHECK(config.mode==0&&config.mask[0]==255&&config.mask[1]==255&&config.mask[2]==255&&config.mask[3]==0);
    CHECK(attaches==(names_valid?1:0));CHECK(atomic_load(&requested)==0);
    if(!result){
        CHECK(current.state==(names_valid?USB_AUDIO_FAULT:USB_AUDIO_STOPPED));
        for(int i=0;i<20;i++)step(1000);
        CHECK(attaches==(names_valid?1:0));CHECK(!uab_boot_initialize(&config));
    }else{
        CHECK(current.state==USB_AUDIO_RUNNING);
        usb_audio_set_enabled(false);step(1);step(300000);CHECK(current.state==USB_AUDIO_STOPPED);
        CHECK(!uab_boot_initialize(&config)); // only a real reboot gets another automatic START
        for(int i=0;i<20;i++)step(1000);
        CHECK(current.state==USB_AUDIO_STOPPED&&attaches==1&&reconnects==0);
        CHECK(!uab_boot_auto_start_pending());
        usb_audio_set_enabled(true);step(1);CHECK(current.state==USB_AUDIO_RUNNING&&attaches==1&&reconnects==1);
    }
    printf("PASS: %u production boot/name-before-attach/one-shot-start checks (%s)\n",checks,argc>1?argv[1]:"saved-names");return 0;
}
