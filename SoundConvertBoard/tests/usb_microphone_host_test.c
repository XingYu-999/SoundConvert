#define UAB_MICROPHONE_FIFO_TEST 1
#include <assert.h>
#include <stdio.h>
#include "../usb/usb_microphone.c"
static unsigned write_limit;static bool force_zero;static uint64_t now=100;static unsigned checks;static uint16_t threshold;static tu_fifo_t ff={.depth=16384};
#define CHECK(x) do{checks++;assert(x);}while(0)
uint32_t time_us_32(void){return (uint32_t)now;}uint64_t time_us_64(void){return now;}
uint16_t tu_fifo_count(tu_fifo_t*f){return f->count;}uint16_t tu_fifo_remaining(tu_fifo_t*f){return f->depth-f->count;}
tu_fifo_t*tud_audio_get_ep_in_ff(void){return &ff;}
uint16_t tud_audio_write(const void*p,uint16_t n){if(force_zero)return 0;if(write_limit&&n>write_limit)n=(uint16_t)write_limit;if(n>ff.depth-ff.count)n=ff.depth-ff.count;memcpy(ff.data+ff.count,p,n);ff.count+=n;return n;}
bool tud_audio_clear_ep_in_ff(void){ff.count=0;return true;}void tud_audio_n_set_ep_in_fifo_threshold(uint8_t func,uint16_t t){(void)func;threshold=t;}
static unsigned transition_fixture(unsigned stride,unsigned limit){
 reset_transition();ff.count=0;state.rate=48000;state.bits=(uint8_t)(stride*8);gain_current=gain_target=1;gain_remaining=0;gain_step=0;
 uint8_t pcm[288];int32_t value=stride==2?10000:2500000;for(unsigned i=0;i<96;i++)for(unsigned b=0;b<stride;b++)pcm[i*stride+b]=(uint8_t)((uint32_t)value>>(8*b));
 write_limit=0;CHECK(write_processed(pcm,96*stride,stride)==96*stride);CHECK(transition_last==value);
 force_zero=true;CHECK(write_concealed(96,stride)==0);CHECK(transition_last==value&&!transition_gap&&transition_remaining==0);force_zero=false;
 write_limit=limit;unsigned frames=0,guard=0;while(frames<96){frames+=write_concealed(96-frames,stride);CHECK(++guard<300);}
 CHECK(transition_gap&&transition_last==0&&transition_remaining==0);
 unsigned at=0;while(at<96*stride){at+=write_processed(pcm+at,96*stride-at,stride);CHECK(++guard<600);}
 CHECK(!transition_gap&&transition_last==value&&transition_remaining==0);
 for(unsigned i=97;i<192;i++)CHECK(read_sample(ff.data+i*stride,stride)<=read_sample(ff.data+(i-1)*stride,stride));
 for(unsigned i=193;i<288;i++)CHECK(read_sample(ff.data+i*stride,stride)>=read_sample(ff.data+(i-1)*stride,stride));
 int32_t maximum_step=0;for(unsigned i=96;i<288;i++){int32_t delta=read_sample(ff.data+i*stride,stride)-read_sample(ff.data+(i-1)*stride,stride);if(delta<0)delta=-delta;if(delta>maximum_step)maximum_step=delta;}
 CHECK(maximum_step<=value/96+1);write_limit=0;return ff.count;
}
int main(void){usb_microphone_init();usb_mic_status_t m;usb_microphone_get_status(&m);uint8_t data[288]={1,2,3};
 CHECK(m.rate==48000&&m.bits==16&&m.formats_mask==1&&!m.active);CHECK(!usb_microphone_enqueue(data,48,48000,16,m.generation,0));
 CHECK(usb_microphone_set_alt(1));usb_microphone_set_network_enabled(true);usb_microphone_get_status(&m);CHECK(m.active&&threshold==96);
 CHECK(!usb_microphone_set_formats(3));CHECK(!usb_microphone_set_alt(2));CHECK(!usb_microphone_enqueue(data,48,48000,16,m.generation-1,0));
 for(unsigned i=0;i<24;i++)CHECK(usb_microphone_enqueue(data,48,48000,16,m.generation,0));
 for(unsigned i=0;i<7;i++){now+=100;usb_microphone_task();}usb_microphone_get_status(&m);CHECK(m.primed&&threshold==2304&&ff.count>=2304&&m.queued_packets==0);
 CHECK(!memcmp(ff.data,data,3));usb_microphone_tx_isr(96);usb_microphone_get_status(&m);usb_microphone_task();usb_microphone_get_status(&m);CHECK(m.tx_frames==48&&m.tx_packets==1);
 usb_microphone_reset();CHECK(usb_microphone_set_formats(USB_MIC_FORMAT_ALL));CHECK(usb_microphone_set_alt(9));usb_microphone_get_status(&m);CHECK(m.rate==96000&&m.bits==24&&threshold==288);
 for(unsigned i=0;i<64;i++)CHECK(usb_microphone_enqueue(data,96,96000,24,m.generation,0));CHECK(!usb_microphone_enqueue(data,96,96000,24,m.generation,0));usb_microphone_task();usb_microphone_get_status(&m);CHECK(m.queue_drops==1&&m.primed);
 uint32_t previous=m.generation;usb_microphone_reset();CHECK(usb_microphone_set_alt(1));usb_microphone_set_network_enabled(true);usb_microphone_get_status(&m);CHECK(m.generation>previous&&!usb_microphone_enqueue(data,96,96000,24,previous,0));

 usb_microphone_reset();ff.count=0;uint8_t pcm16[192];for(unsigned i=0;i<96;i++){pcm16[i*2]=0x10;pcm16[i*2+1]=0x27;}
 CHECK(write_processed(pcm16,192,2)==192&&!memcmp(ff.data,pcm16,192));
 ff.count=0;CHECK(usb_microphone_set_control(-6*256,false));write_limit=12;unsigned at=0;
 while(at<192){unsigned wrote=write_processed(pcm16+at,192-at,2);CHECK(wrote==12);at+=wrote;}
 CHECK(gain_remaining==0);int16_t last=(int16_t)(ff.data[190]|(unsigned)ff.data[191]<<8);CHECK(last>=5010&&last<=5013);
 CHECK((ff.data[0]|(unsigned)ff.data[1]<<8)>last);write_limit=0;
 CHECK(usb_microphone_set_control(0,true));ff.count=0;CHECK(write_processed(pcm16,192,2)==192);CHECK(ff.data[190]==0&&ff.data[191]==0);
 ff.count=0;CHECK(write_processed(pcm16,192,2)==192);for(unsigned i=0;i<192;i++)CHECK(ff.data[i]==0);
 CHECK(usb_microphone_set_formats(USB_MIC_FORMAT_ALL));CHECK(usb_microphone_set_alt(9));CHECK(usb_microphone_set_control(0,false));uint8_t pcm24[288];for(unsigned i=0;i<288;i++)pcm24[i]=(uint8_t)(i*71);
 ff.count=0;CHECK(write_processed(pcm24,288,3)==288);ff.count=0;CHECK(write_processed(pcm24,288,3)==288&&!memcmp(ff.data,pcm24,288));
 CHECK(!usb_microphone_set_control(-61*256,false));CHECK(usb_microphone_set_control(INT16_MIN,false));
 usb_microphone_reset();CHECK(usb_microphone_request_control(-12*256,true));usb_microphone_task();usb_mic_control_t control;usb_microphone_get_control(&control);CHECK(control.volume_q8==-12*256&&control.mute&&control.from_network);
 CHECK(usb_microphone_set_control(0,false));usb_microphone_get_control(&control);CHECK(control.volume_q8==0&&!control.mute&&!control.from_network);
 for(unsigned stride=2;stride<=3;stride++){
  uint8_t expected[864];unsigned total=transition_fixture(stride,0);memcpy(expected,ff.data,total);
  CHECK(transition_fixture(stride,5)==total);CHECK(!memcmp(expected,ff.data,total));
  CHECK(transition_fixture(stride,stride)==total);CHECK(!memcmp(expected,ff.data,total));
 }
 CHECK(usb_microphone_set_alt(1));usb_microphone_set_network_enabled(false);
 for(unsigned i=0;i<20;i++)usb_microphone_task();usb_microphone_get_status(&m);
 CHECK(atomic_load(&wr)==atomic_load(&rd));ff.count=0;usb_microphone_set_network_enabled(true);
 uint8_t signal[96];for(unsigned i=0;i<48;i++){signal[i*2]=0x10;signal[i*2+1]=0x27;}
 for(unsigned i=0;i<24;i++)CHECK(usb_microphone_enqueue(signal,48,48000,16,m.generation,i==1?48:0));
 uint32_t concealed_before=m.concealed_frames;write_limit=5;
 for(unsigned i=0;i<1000;i++){usb_microphone_task();if(!pending_valid&&atomic_load(&wr)==atomic_load(&rd)&&!staged_bytes)break;}
 usb_microphone_get_status(&m);CHECK(m.concealed_frames-concealed_before==48);CHECK(!pending_valid&&!staged_bytes&&m.queued_packets==0);CHECK(ff.count==(24*48+48)*2);CHECK(read_sample(ff.data+ff.count-2,2)==10000);write_limit=0;
 // Stop while one sample is partially written, then restart while startup
 // silence is partially written. Neither transition can strand the stage.
 ff.count=0;reset_transition();write_limit=5;pending_valid=true;CHECK(write_processed(signal,96,2)==5);usb_microphone_set_network_enabled(false);
 for(unsigned i=0;i<100&&staged_bytes;i++)usb_microphone_task();CHECK(!pending_valid&&!staged_bytes&&ff.count==96);
 usb_microphone_tx_isr(96);usb_microphone_task();CHECK(staged_bytes&&staged_gap);
 CHECK(usb_microphone_enqueue(signal,48,48000,16,m.generation,0));usb_microphone_set_network_enabled(false);usb_microphone_set_network_enabled(true);
 for(unsigned i=0;i<24;i++)CHECK(usb_microphone_enqueue(signal,48,48000,16,m.generation,0));
 for(unsigned i=0;i<1000;i++){usb_microphone_task();if(!pending_valid&&!staged_bytes&&atomic_load(&wr)==atomic_load(&rd))break;}
 CHECK(!pending_valid&&!staged_bytes&&atomic_load(&wr)==atomic_load(&rd));CHECK(ff.count==(1+1+24)*96);CHECK(read_sample(ff.data+ff.count-2,2)==10000);write_limit=0;
 // A temporary supply pause needs no sample-index hole. Keep its low-FIFO
 // event, producer age and fallback commits separate from explicit gap PLC.
 CHECK(usb_microphone_set_alt(1));usb_microphone_set_network_enabled(false);
 for(unsigned i=0;i<20;i++)usb_microphone_task();ff.count=0;usb_microphone_set_network_enabled(true);usb_microphone_get_status(&m);
 now=(uint64_t)UINT32_MAX+1000;uint64_t last_enqueue=0;
 for(unsigned i=0;i<24;i++){now+=1000;last_enqueue=now;CHECK(usb_microphone_enqueue(signal,48,48000,16,m.generation,0));}
 for(unsigned i=0;i<7;i++){now+=100;usb_microphone_task();}usb_microphone_get_status(&m);CHECK(m.primed&&m.queued_packets==0&&ff.count==2304);
 uint32_t underruns_before=m.underruns,explicit_before=m.concealed_frames,fallback_before=m.fallback_concealed_frames;
 ff.count=96;usb_microphone_tx_isr(96);now+=25000;usb_microphone_task();usb_microphone_get_status(&m);
 CHECK(m.underruns==underruns_before+1&&m.concealed_frames==explicit_before&&m.fallback_concealed_frames==fallback_before+48);
 CHECK(m.last_underrun_us64==now&&m.last_underrun_us64>UINT32_MAX&&m.last_underrun_fifo_bytes==96&&m.last_underrun_target_bytes==2304);
 CHECK(m.last_underrun_task_gap_us==25000&&m.maximum_task_gap_us>=25000&&m.maximum_enqueue_gap_us>=1000);
 CHECK(m.last_underrun_enqueue_timing_valid&&m.last_underrun_enqueue_age_us==(uint32_t)(now-last_enqueue)&&m.last_underrun_queued_packets==0&&m.last_underrun_pending_bytes==0&&m.last_underrun_staged_bytes==0&&m.last_underrun_tx_packets==1);
 // Partial packed-sample writes count only complete committed fallback frames,
 // including continuations through the shared staged-block path.
 ff.count=0;write_limit=5;usb_microphone_tx_isr(96);now+=1000;usb_microphone_task();usb_microphone_get_status(&m);
 CHECK(staged_bytes&&m.fallback_concealed_frames==fallback_before+48+2);
 for(unsigned i=0;i<100&&staged_bytes;i++){now+=100;usb_microphone_task();}
 usb_microphone_get_status(&m);CHECK(!staged_bytes&&m.fallback_concealed_frames==fallback_before+96&&m.concealed_frames==explicit_before&&m.underruns==underruns_before+1);write_limit=0;
 // Core0 diagnostics never spin waiting for an in-progress Core1 mailbox.
 ff.count=96;state.primed=true;atomic_fetch_add(&enqueue_version,1);now+=1000;usb_microphone_task();usb_microphone_get_status(&m);atomic_fetch_add(&enqueue_version,1);
 CHECK(!m.last_underrun_enqueue_timing_valid&&m.last_underrun_enqueue_age_us==UINT32_MAX);
 // A pending partial source block is still the original underrun guard.
 uint32_t guarded=m.underruns;ff.count=96;state.primed=true;pending_valid=true;pending_at=0;pending.bytes=96;memcpy(pending.pcm,signal,96);force_zero=true;now+=1000;usb_microphone_task();usb_microphone_get_status(&m);
 CHECK(m.underruns==guarded&&pending_valid&&staged_bytes);force_zero=false;reset_transition();pending_valid=false;
 // Packed 24-bit fallback commits may stop inside a sample: 5, 10 and 12
 // accepted bytes represent 1, 3 and 4 whole frames, never generated frames.
 ff.count=0;state.bits=24;state.rate=48000;write_limit=5;uint32_t fallback24=state.fallback_concealed_frames,explicit24=state.concealed_frames;
 force_zero=true;write_fallback(4,3);CHECK(state.fallback_concealed_frames==fallback24);force_zero=false;
 write_fallback(4,3);CHECK(state.fallback_concealed_frames==fallback24+1&&staged_at==5);
 write_fallback(4,3);CHECK(state.fallback_concealed_frames==fallback24+3&&staged_at==10);
 write_fallback(4,3);CHECK(state.fallback_concealed_frames==fallback24+4&&!staged_bytes&&state.concealed_frames==explicit24);write_limit=0;
 printf("PASS: %u production microphone queue/format/prefill/overflow/generation/FIFO checks\n",checks);return 0;}
