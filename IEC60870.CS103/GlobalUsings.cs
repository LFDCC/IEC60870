//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  本文件集中声明全局 using：各源文件无需重复引入常用命名空间
//------------------------------------------------------------------------------

global using System;
global using System.Buffers;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.IO;
global using System.IO.Ports;
global using System.Linq;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using TouchSocket.Core;
global using TouchSocket.Sockets;

// 注意：这里刻意不 global using IEC60870.Core。
// 103 不依赖 Core（见 csproj 说明）；若引入会把 Core 的 ASDU/CP56Time2a/TypeID
// 与 103 的 Asdu/Cp56Time2a/TypeIds 同时带入每个编译单元，
// 形成只差大小写、语义却完全不同的近似名（写错一个字母仍能编译通过）。
