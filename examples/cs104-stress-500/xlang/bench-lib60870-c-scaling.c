#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <windows.h>
#include "iec60870_common.h"
#include "cs101_information_objects.h"
#include "buffer_frame.h"

void CS101_ASDU_encode(CS101_ASDU self, Frame frame);

static struct sCS101_AppLayerParameters al = {
    1, 1, 2, 0, 2, 3, 249};

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
static int g_points = 8;

static double bench_encode(int iters)
{
    volatile float sink = 0;
    double t0 = now_ms();
    for (int i = 0; i < iters; i++)
    {
        CS101_ASDU asdu = CS101_ASDU_create(&al, false, CS101_COT_SPONTANEOUS, 0, 1, false, false);
        for (int p = 0; p < g_points; p++)
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

static double bench_decode(int iters)
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

static void build_wire(void)
{
    CS101_ASDU a = CS101_ASDU_create(&al, false, CS101_COT_SPONTANEOUS, 0, 1, false, false);
    for (int p = 0; p < g_points; p++)
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

int main(int argc, char** argv)
{
    int iters = (argc > 1) ? atoi(argv[1]) : 1000000;
    printf("lib60870-C scaling bench, iters=%d\n", iters);
    printf("%-8s %10s %12s %10s %12s %12s\n", "points", "wireB", "enc us/op", "ns/pt", "dec us/op", "ns/pt");
    int sizes[] = {1, 4, 8, 16, 32};
    for (int s = 0; s < 5; s++)
    {
        g_points = sizes[s];
        build_wire();
        double be = 1e18, bd = 1e18;
        for (int r = 0; r < 5; r++)
        {
            double v = bench_encode(iters); if (v < be) be = v;
            v = bench_decode(iters); if (v < bd) bd = v;
        }
        printf("%-8d %10d %12.3f %12.1f %12.3f %12.1f\n", g_points, wire_len, be, be * 1000.0 / g_points, bd, bd * 1000.0 / g_points);
    }
    return 0;
}
