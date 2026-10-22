#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "cJSON.h"
#include "../uab_json_print.h"
static unsigned checks;
static void service(void*context){++*(unsigned*)context;}
#define CHECK(x) do{checks++;assert(x);}while(0)
static void roundtrip(double value,const char*expected){cJSON*n=cJSON_CreateNumber(value);char*json=cJSON_PrintUnformatted(n);CHECK(json!=NULL);if(expected)CHECK(!strcmp(json,expected));cJSON*read=cJSON_Parse(json);CHECK(cJSON_IsNumber(read)&&read->valuedouble==value);cJSON_Delete(read);cJSON_free(json);cJSON_Delete(n);}
int main(void){roundtrip(4294967295.0,"4294967295");roundtrip(2147483648.0,"2147483648");roundtrip(-7735/256.0,"-30.21484375");roundtrip(1/256.0,"0.00390625");roundtrip(-0.5,"-0.5");roundtrip(65535.99609375,NULL);roundtrip(-65535.99609375,"-65535.99609375");roundtrip(0,"0");roundtrip(-60,"-60");roundtrip(0.1,NULL);
 // Every legal UAC1 Host volume must survive the production serializer exactly.
 for(int q=-60*256;q<=0;q++)roundtrip(q/256.0,NULL);
 cJSON*tree=cJSON_Parse("{\"unicode\":\"音频\\n\\\"\\\\\",\"nested\":[null,true,false,{\"q8\":-30.21484375,\"u32\":4294967295}],\"empty\":{}}");CHECK(tree!=NULL);
 char reference[2048],yielded[2048];unsigned calls=0;CHECK(cJSON_PrintPreallocated(tree,reference,sizeof(reference),0));CHECK(uab_json_print(tree,yielded,sizeof(yielded),service,&calls));CHECK(!strcmp(reference,yielded)&&calls>10);CHECK(!uab_json_print(tree,yielded,8,service,&calls));cJSON_Delete(tree);
 printf("PASS: %u production JSON exact-decimal and yielding serializer checks\n",checks);return 0;}
