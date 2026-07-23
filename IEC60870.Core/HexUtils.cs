/*
 *  Copyright 2026 LFDCC
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

// 调试辅助：高性能字节数组 → 16 进制字符串
//
// 作为 IEC60870.Core 的内置扩展方法，所有引用本库的项目（CS101 / CS104 / 示例 / 业务代码）
// 均可直接使用，无需各自实现：
//
//   byte[] buf = ...;
//   string hex = buf.ToHex();           // 默认大写, 空格分隔
//   string hexL = buf.ToHex(false);     // 小写
//
// 实现要点：使用 string.Create 一次性分配目标字符串，热路径零堆分配；
// 预置 HexUpper / HexLower 两个 char[] 常量，避免重复 ToString("X2") 装箱。

using System;

namespace IEC60870.Core;

/// <summary>
/// 字节数组与 16 进制字符串互转的调试辅助扩展方法（IEC60870.Core 内置，全局可用）。
/// </summary>
public static class HexUtils
{
    #region 字节数组转16进制字符串

    private static readonly char[] HexUpper = "0123456789ABCDEF".ToCharArray();
    private static readonly char[] HexLower = "0123456789abcdef".ToCharArray();

    /// <summary>
    /// 字节数组转16进制字符串（每字节两位，空格分隔）。
    /// 使用 string.Create 一次性分配目标字符串，热路径零堆分配。
    /// </summary>
    /// <param name="frame">字节数组</param>
    /// <param name="upperCase">是否使用大写字母，默认 true</param>
    /// <returns>16进制字符串（空输入返回空串）</returns>
    public static string ToHex(this byte[] frame, bool upperCase = true)
    {
        if (frame == null || frame.Length == 0) return string.Empty;

        var chars = upperCase ? HexUpper : HexLower;
        var n = frame.Length;

        return string.Create(n * 3 - 1, (frame, chars), (dst, state) =>
        {
            var (src, hex) = state;
            var di = 0;
            for (var i = 0; i < src.Length; i++)
            {
                if (i > 0) dst[di++] = ' ';
                dst[di++] = hex[src[i] >> 4];
                dst[di++] = hex[src[i] & 0x0F];
            }
        });
    }

    #endregion
}
