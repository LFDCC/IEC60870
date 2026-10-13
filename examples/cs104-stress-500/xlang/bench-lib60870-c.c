#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <windows.h>
#include "iec60870_common.h"
#include "cs101_information_objects.h"
#include "buffer_frame.h"

/* 库内部函数（tests/all_tests.c 同法声明） */
void CS101_ASDU_encode(CS101_ASDU self, Frame frame);

static struct sCS101_AppLayerParameters al = {
    1 /*sizeOfTypeId*/, 1 /*sizeOfVSQ*/, 2 /*sizeOfCOT*/, 0 /*originatorAddress*/,
    2 /*sizeOfCA*/, 3 /*sizeOfIOA*/, 249 /*maxSizeOfASDU*/};

static double now_ms(void)
{
    static LARGE_INTEGER f;
    static int init = 0;
    if (!init) { QueryPerformanceFrequency(&f); init = 1; }
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart * 1000.0;
}

static uint8_t buf[512];
static uint8_t wire[512];
static int wire_len;

static double bench_encode_me_nc(int iters)
{
    volatile float sink = 0;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++)
    {
        CS101_ASDU asdu = CS101_ASDU_create(&al, false, CS101_COT_SPONTANEOUS, 0, 1, false, false);
        for (int p = 0; p < 8; p++)
        {
            MeasuredValueShort mv = MeasuredValueShort_create(NULL, 1000 + p, p * 0.5f, IEC60870_QUALITY_GOOD);
            CS101_ASDU_addInformationObject(asdu, (InformationObject)mv);
            MeasuredValueShort_destroy(mv);
        }
        struct sBufferFrame bf;
        Frame f = BufferFrame_initialize(&bf, buf, 0, sizeof(buf));
        CS101_ASDU_encode(asdu, f);
        sink += (float)Frame_getMsgSize(f);
        CS101_ASDU_destroy(asdu);
    }
    return (now_ms() - t0) * 1000.0 / iters;
}

static double bench_encode_sp_na(int iters)
{
    volatile float sink = 0;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++)
    {
        CS101_ASDU asdu = CS101_ASDU_create(&al, false, CS101_COT_SPONTANEOUS, 0, 1, false, false);
        SinglePointInformation sp = SinglePointInformation_create(NULL, 1000, true, IEC60870_QUALITY_GOOD);
        CS101_ASDU_addInformationObject(asdu, (InformationObject)sp);
        SinglePointInformation_destroy(sp);
        struct sBufferFrame bf;
        Frame f = BufferFrame_initialize(&bf, buf, 0, sizeof(buf));
        CS101_ASDU_encode(asdu, f);
        sink += (float)Frame_getMsgSize(f);
        CS101_ASDU_destroy(asdu);
    }
    return (now_ms() - t0) * 1000.0 / iters;
}

static double bench_encode_se_nc(int iters)
{
    volatile float sink = 0;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++)
    {
        CS101_ASDU asdu = CS101_ASDU_create(&al, false, CS101_COT_ACTIVATION, 0, 1, false, false);
        SetpointCommandShort sc = SetpointCommandShort_create(NULL, 3000, 123.456f, false, 0);
        CS101_ASDU_addInformationObject(asdu, (InformationObject)sc);
        SetpointCommandShort_destroy(sc);
        struct sBufferFrame bf;
        Frame f = BufferFrame_initialize(&bf, buf, 0, sizeof(buf));
        CS101_ASDU_encode(asdu, f);
        sink += (float)Frame_getMsgSize(f);
        CS101_ASDU_destroy(asdu);
    }
    return (now_ms() - t0) * 1000.0 / iters;
}

static double bench_decode_me_nc(int iters)
{
    volatile float sink = 0;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++)
    {
        CS101_ASDU asdu = CS101_ASDU_createFromBuffer(&al, wire, wire_len);
        int n = CS101_ASDU_getNumberOfElements(asdu);
        for (int e = 0; e < n; e++)
        {
            MeasuredValueShort mv = (MeasuredValueShort)CS101_ASDU_getElement(asdu, e);
            sink += MeasuredValueShort_getValue(mv);
            MeasuredValueShort_destroy(mv);
        }
        CS101_ASDU_destroy(asdu);
    }
    return (now_ms() - t0) * 1000.0 / iters;
}

int main(int argc, char** argv)
{
    int iters = (argc > 1) ? atoi(argv[1]) : 2000000;
    printf("lib60870-C (mz-automation reference) protocol-core microbench\n");
    printf("iterations: %d\n", iters);

    /* pre-build wire bytes for decode bench */
    {
        CS101_ASDU a = CS101_ASDU_create(&al, false, CS101_COT_SPONTANEOUS, 0, 1, false, false);
        for (int p = 0; p < 8; p++)
        {
            MeasuredValueShort mv = MeasuredValueShort_create(NULL, 1000 + p, p * 0.5f, IEC60870_QUALITY_GOOD);
            CS101_ASDU_addInformationObject(a, (InformationObject)mv);
            MeasuredValueShort_destroy(mv);
        }
        struct sBufferFrame bf;
        Frame f = BufferFrame_initialize(&bf, wire, 0, sizeof(wire));
        CS101_ASDU_encode(a, f);
        wire_len = Frame_getMsgSize(f);
        CS101_ASDU_destroy(a);
    }

#define RUN(name, fn) do { \
    double best = 1e18, sum = 0; \
    for (int r = 0; r < 7; r++) { double v = fn(iters); sum += v; if (v < best) best = v; } \
    printf("%-30s best %8.3f  avg %8.3f us/op\n", name, best, sum / 7.0); \
} while (0)

    RUN("encode M_ME_NC_1 x8", bench_encode_me_nc);
    RUN("encode M_SP_NA_1 x1", bench_encode_sp_na);
    RUN("encode C_SE_NC_1", bench_encode_se_nc);
    RUN("decode M_ME_NC_1 x8", bench_decode_me_nc);
    return 0;
}
