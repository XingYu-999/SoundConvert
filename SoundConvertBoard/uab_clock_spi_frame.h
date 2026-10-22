#pragma once
#include <stddef.h>
#include <stdint.h>

// W5500 VDM: address high/low/control, followed by exactly the bounded payload.
// The clock owner uses Pico SDK spi_write_read_blocking directly; its existing
// RAM implementation caps in-flight data at the eight-byte hardware RX FIFO.
#define UAB_CLOCK_SPI_MAX_BYTES 112u
#define UAB_CLOCK_SPI_FRAME_BYTES (3u+UAB_CLOCK_SPI_MAX_BYTES)
// Dedicated BSS read-only-TX scratch keeps bytes 3..114 zero for its lifetime.
// Updating only the address avoids an optimizer-generated Flash memset call.
static inline unsigned uab_clock_spi_pack_zeroed_read(uint32_t address,unsigned bytes,uint8_t *frame,size_t capacity){
 if(bytes>UAB_CLOCK_SPI_MAX_BYTES||capacity<bytes+3u)return 0;
 frame[0]=(uint8_t)(address>>16);frame[1]=(uint8_t)(address>>8);frame[2]=(uint8_t)address&0xf8u;
 return bytes+3u;
}
static inline unsigned uab_clock_spi_pack_read(uint32_t address,unsigned bytes,uint8_t *frame,size_t capacity){
 if(bytes>UAB_CLOCK_SPI_MAX_BYTES||capacity<bytes+3u)return 0;
 frame[0]=(uint8_t)(address>>16);frame[1]=(uint8_t)(address>>8);frame[2]=(uint8_t)address&0xf8u;
 for(unsigned i=0;i<bytes;i++)frame[i+3]=0;
 return bytes+3u;
}
static inline unsigned uab_clock_spi_pack_write(uint32_t address,const uint8_t *data,unsigned bytes,uint8_t *frame,size_t capacity){
 if(bytes>UAB_CLOCK_SPI_MAX_BYTES||capacity<bytes+3u||(!data&&bytes))return 0;
 frame[0]=(uint8_t)(address>>16);frame[1]=(uint8_t)(address>>8);frame[2]=((uint8_t)address&0xf8u)|0x04u;
 for(unsigned i=0;i<bytes;i++)frame[i+3]=data[i];
 return bytes+3u;
}
static inline void uab_clock_spi_unpack_read(const uint8_t *frame,uint8_t *data,unsigned bytes){for(unsigned i=0;i<bytes;i++)data[i]=frame[i+3];}
