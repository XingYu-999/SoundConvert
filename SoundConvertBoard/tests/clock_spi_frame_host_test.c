#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_clock_spi_frame.h"
static unsigned checks;
#define CHECK(x) do{checks++;assert(x);}while(0)
int main(void){
 uint8_t frame[UAB_CLOCK_SPI_FRAME_BYTES],received[UAB_CLOCK_SPI_FRAME_BYTES],payload[UAB_CLOCK_SPI_MAX_BYTES],decoded[UAB_CLOCK_SPI_MAX_BYTES];
 for(unsigned i=0;i<sizeof(payload);i++)payload[i]=(uint8_t)(i*71u+9u);
 const uint32_t addresses[]={0x00012fu,0x00260cu,0xffff1cu,0x123418u};
 for(unsigned a=0;a<sizeof(addresses)/sizeof(addresses[0]);a++)for(unsigned bytes=0;bytes<=UAB_CLOCK_SPI_MAX_BYTES;bytes++){
  uint32_t address=addresses[a];memset(frame,0x77,sizeof(frame));CHECK(uab_clock_spi_pack_read(address,bytes,frame,sizeof(frame))==bytes+3);
  CHECK(frame[0]==(uint8_t)(address>>16)&&frame[1]==(uint8_t)(address>>8)&&frame[2]==((uint8_t)address&0xf8u));
  for(unsigned i=0;i<bytes;i++)CHECK(frame[i+3]==0);
  if(bytes<UAB_CLOCK_SPI_MAX_BYTES)CHECK(frame[bytes+3]==0x77);
  CHECK(uab_clock_spi_pack_write(address,payload,bytes,frame,sizeof(frame))==bytes+3);
  CHECK(frame[2]==(((uint8_t)address&0xf8u)|4u)&&!memcmp(frame+3,payload,bytes));
  received[0]=0xa1;received[1]=0xb2;received[2]=0xc3;memcpy(received+3,payload,bytes);memset(decoded,0,sizeof(decoded));uab_clock_spi_unpack_read(received,decoded,bytes);CHECK(!memcmp(decoded,payload,bytes));
 }
 CHECK(!uab_clock_spi_pack_read(0,113,frame,sizeof(frame)));CHECK(!uab_clock_spi_pack_write(0,payload,113,frame,sizeof(frame)));
 CHECK(!uab_clock_spi_pack_read(0,112,frame,114));CHECK(!uab_clock_spi_pack_write(0,payload,112,frame,114));CHECK(!uab_clock_spi_pack_write(0,NULL,1,frame,sizeof(frame)));
 // Production uses a distinct BSS read TX buffer: subsequent register/bulk
 // reads alter only its three header bytes, with no payload clearing required.
 uint8_t read_tx[UAB_CLOCK_SPI_FRAME_BYTES]={0};
 for(unsigned bytes=0;bytes<=UAB_CLOCK_SPI_MAX_BYTES;bytes++){
  uint32_t address=0xffff1cu+((uint32_t)bytes<<8);CHECK(uab_clock_spi_pack_zeroed_read(address,bytes,read_tx,sizeof(read_tx))==bytes+3);
  CHECK(read_tx[0]==(uint8_t)(address>>16)&&read_tx[1]==(uint8_t)(address>>8)&&read_tx[2]==((uint8_t)address&0xf8u));
  for(unsigned i=3;i<sizeof(read_tx);i++)CHECK(read_tx[i]==0);
  CHECK(uab_clock_spi_pack_write(address,payload,bytes,frame,sizeof(frame))==bytes+3);for(unsigned i=3;i<sizeof(read_tx);i++)CHECK(read_tx[i]==0);
 }
 CHECK(!uab_clock_spi_pack_zeroed_read(0,113,read_tx,sizeof(read_tx)));CHECK(!uab_clock_spi_pack_zeroed_read(0,112,read_tx,114));
 printf("PASS: %u bounded clock SPI address/control/dummy/full-duplex frame/length checks (host only; no hardware timing claim)\n",checks);return 0;
}
