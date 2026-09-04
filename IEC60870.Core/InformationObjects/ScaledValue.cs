//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 带符号 16 位缩放值（VTI/SV 编码辅助）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 缩放值（Scaled Value），以 2 字节小端承载有符号整数，并支持与归一化浮点值的互转。
/// 取值范围与 IEC 60870-5 测量值约定一致：有符号 16 位（<c>-32768 … 32767</c>）。
/// </summary>
public class ScaledValue
{
    private readonly byte[] _encodedValue = new byte[2];

    // 取值范围与互转比例常数（由标准定义，行为必须保持）。
    private const int SCALED_VALUE_MAX = 32767;
    private const int SCALED_VALUE_MIN = -32768;
    private const float NORMALIZED_VALUE_MAX = 32767f / 32768f;
    private const float NORMALIZED_VALUE_MIN = -1.0f;
    private const double NORMALIZED_SCALE = 32767.0;
    private const float SCALED_SCALE = 32768f;

    /// <summary>从报文切片解析 2 字节缩放值。</summary>
    /// <exception cref="ASDUParsingException">报文剩余长度不足 2 字节时抛出。</exception>
    public ScaledValue(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + 2)
        {
            throw new ASDUParsingException("Message too small for parsing ScaledValue");
        }

        for (var j = 0; j < 2; j++)
        {
            _encodedValue[j] = msg[startIndex + j];
        }
    }

    /// <summary>构造零值缩放值。</summary>
    public ScaledValue()
    {
    }

    /// <summary>以 32 位整数构造，超出范围将被钳位。</summary>
    public ScaledValue(int value) => Value = value;

    /// <summary>以 16 位整数构造。</summary>
    public ScaledValue(short value) => ShortValue = value;

    /// <summary>以另一实例复制构造。</summary>
    public ScaledValue(ScaledValue original) => ShortValue = original.ShortValue;

    /// <summary>返回编码字节的副本。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>以零分配方式返回编码字节切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    /// <summary>有符号缩放值（范围 <c>-32768 … 32767</c>）。</summary>
    public int Value
    {
        get
        {
            var raw = _encodedValue[0] | (_encodedValue[1] << 8);

            // 对负数做符号扩展（最高位为 1 时减去 0x10000）。
            if (raw > SCALED_VALUE_MAX)
            {
                raw -= 0x10000;
            }

            return Clamp(raw, SCALED_VALUE_MIN, SCALED_VALUE_MAX);
        }
        set
        {
            var clamped = Clamp(value, SCALED_VALUE_MIN, SCALED_VALUE_MAX);

            _encodedValue[0] = (byte)(clamped & 0xFF);
            _encodedValue[1] = (byte)((clamped >> 8) & 0xFF);
        }
    }

    /// <summary>无符号 16 位视图（直接按小端解析，不做符号扩展）。</summary>
    public short ShortValue
    {
        get
        {
            ushort raw = _encodedValue[0];
            raw += (ushort)(_encodedValue[1] * 0x100);

            return (short)raw;
        }
        set
        {
            var raw = (ushort)value;

            _encodedValue[0] = (byte)(raw % 256);
            _encodedValue[1] = (byte)(raw / 256);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => "" + Value;

    /// <summary>转换为归一化浮点值（范围约 <c>-1.0 … 32767/32768</c>），结果做钳位。</summary>
    public float GetNormalizedValue()
    {
        var result = Value / NORMALIZED_SCALE;

        if (result > NORMALIZED_VALUE_MAX)
        {
            result = NORMALIZED_VALUE_MAX;
        }

        if (result < NORMALIZED_VALUE_MIN)
        {
            result = NORMALIZED_VALUE_MIN;
        }

        return (float)result;
    }

    /// <summary>归一化浮点值 → 缩放整数（四舍五入，符号相关）。</summary>
    public int ConvertNormalizedValueToScaled(float value)
    {
        var clamped = Clamp(value, NORMALIZED_VALUE_MIN, NORMALIZED_VALUE_MAX);

        var product = clamped * SCALED_SCALE;

        return (int)(product < 0 ? product - 0.5f : product + 0.5f);
    }

    /// <summary>由归一化浮点值设置缩放值（四舍五入，符号相关）。</summary>
    public void SetScaledFromNormalizedValue(float value)
    {
        var clamped = Clamp(value, NORMALIZED_VALUE_MIN, NORMALIZED_VALUE_MAX);

        var product = clamped * SCALED_SCALE;

        Value = (int)(product < 0 ? product - 0.5f : product + 0.5f);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
