# IEC60870.NET (Async)

![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

IEC 60870-5-101 / 103 / 104 protocol library for **.NET 6**, fully refactored for
asynchronous operation on top of [TouchSocket](https://github.com/RRQM/TouchSocket).

This library implements the IEC 60870-5-101 / 103 / 104 protocol stack for .NET,
built around `Task` / `ValueTask`, `Span<byte>`, and pooled, zero-allocation
buffering, with a familiar information-object model. It is released under the
**MIT License**.

> Attribution: this project was originally derived from
> [lib60870.NET](https://github.com/mz-automation/lib60870.net) by MZ Automation
> (Michael Zillgith), which is licensed under GPLv3. The protocol logic has
> since been substantially rewritten and restructured for async operation; the
> original copyright notices have been removed from the source. See the git
> history and the upstream project for provenance.

## Features

- **Fully asynchronous** client/server APIs (`ConnectAsync`, `SendAsync`,
  `BroadcastAsync`, …) — no blocking socket calls anywhere on the hot path.
- **Low / zero GC pressure**: received bytes flow
  `ByteBlock → Span → AsduView → callback`; outgoing frames are
  built with a pooled `ValueByteBlock` and returned to the array pool after
  the send completes.
- **Four independent packages** so you only ship what you use:
  `Core` (transport-agnostic protocol), `CS101` (101 link layer),
  `CS104` (104 transport).
- **Vendor private types** via `IPrivateIOFactory` — register custom TypeIDs
  (e.g. Xuji 166/168 fault reports) and let the decoder handle them.
- **Built-in debug helpers**:
  - `byte[].ToHex()` — high-performance hex string (`IEC60870.Core`).
  - `byte[]` / `ASDU` → `ToTelegram()` — renders APCI / ASDU / raw frame text
    for logging (`IEC60870.CS104`).
- **TLS support** for CS104 via TouchSocket's SSL transport.
- Libraries target `net6.0`; examples target `net8.0`.

## Packages

| Package | Description |
| --- | --- |
| `LFDCC.IEC60870.Core`  | Transport-agnostic protocol core: `ASDU`, information objects, time/quality types, hex & telegram helpers. |
| `LFDCC.IEC60870.CS101` | IEC 60870-5-101 link layer (balanced / unbalanced, serial & TCP) on TouchSocket. |
| `LFDCC.IEC60870.CS103` | IEC 60870-5-103 / DL/T 667 protection-equipment stack: native FT1.2 over serial & TCP, vendor-private type extensions. |
| `LFDCC.IEC60870.CS104` | IEC 60870-5-104 TCP / TLS transport on TouchSocket. |

```bash
dotnet add package LFDCC.IEC60870.CS104
```

> `CS101` and `CS104` depend on `LFDCC.IEC60870.Core`; NuGet resolves and
> installs it automatically. **`CS103` does not** — its ASDU model (FUN/INF
> addressing), COT/TypeID value spaces and time-stamp semantics all differ from
> 101/104, so it shares no application layer and can be installed on its own.

## Supported protocols

| Standard | Transport | Library |
| --- | --- | --- |
| IEC 60870-5-104 | TCP / TLS | `IEC60870.CS104` |
| IEC 60870-5-101 | Serial (FT1.2), balanced & unbalanced, and TCP | `IEC60870.CS101` |
| IEC 60870-5-103 (DL/T 667) | Serial (FT1.2) and TCP — **native 103, no APCI** | `IEC60870.CS103` |

> **103 vs 104**: 103 is *not* 101/104 with a different port. 103 uses native
> FT1.2 frames (`0x10` / `0x68 L L 68` / `0xE5`) over both serial and TCP, with
> `FUN`/`INF` two-level addressing instead of IOA. The `0x68` start byte is shared
> with 104's APCI by coincidence only — the second byte, control field and frame
> terminator all differ. `CS103` shares no protocol code with `CS104`.

## Getting started (IEC 60870-5-103)

### Master over serial

```csharp
using System.IO.Ports;
using IEC60870.CS103;

// 103 field convention: 9600 baud, 8 data bits, EVEN parity, 1 stop bit
using var port = new SerialPort("COM1", 9600, Parity.Even, 8, StopBits.One);
await using var client = new Iec103Client(port);

await client.ConnectAsync();
await client.ResetLinkAsync(1);                       // RESET CU
await client.SendClockSyncAsync(1, DateTime.Now);     // TI=6
var points = await client.GeneralInterrogationAsync(1); // TI=7 → TI=8
```

### Master over TCP

```csharp
await using var client = new Iec103Client("10.1.1.5", 2404);
await client.ConnectAsync();
await client.ResetLinkAsync(1);
```

### Slave over serial (RS-485, with address filtering)

```csharp
using var port = new SerialPort("COM2", 9600, Parity.Even, 8, StopBits.One);

var options = new Iec103Options()
    .UseLinkParameters(l =>
    {
        l.LocalLinkAddress = 1;     // only answer frames for this address…
        l.AcceptBroadcast = true;   // …plus 255 broadcast
    });

await using var server = new Iec103Server(options);
server.AsduReceived += (session, in asdu) => { /* reply via session.SendAsduAsync */ };
await server.StartAsync(port);
```

### Slave over TCP (multi-master)

```csharp
await using var server = new Iec103Server();
server.AsduReceived += (session, in asdu) => { /* … */ };
await server.StartAsync(2404);   // multiple masters connect concurrently
```

### Vendor-private types

Protection devices routinely extend 103 with private types. The registry covers
five axes — `TypeID`, `FUN`+`INF`, `FUN` ranges, generic-classification
`GIN`/`KOD`/`GDD`, and `COT`:

```csharp
// 私有类型只需登记（决定该 TI 算「认识」，否则会走 UnknownAsduReceived）
client.PrivateTypes.RegisterFunInf(fun: 200, inf: 7, new MyVendorHandler());

client.AsduReceived += (in Iec103AsduView asdu) =>
{
    switch (asdu.TypeId)
    {
        case 200:            // 你的私有类型，自己解析
            HandleVendorObject(ParseMyVendorObject(in asdu));
            break;

        default:             // 标准类型交给库
            if (Iec103AsduDecoder.TryDecode(in asdu, out var decoded, out _))
                HandleStandard(decoded!);
            break;
    }
};
```

Anything not registered is **never dropped**: it surfaces as
`Iec103UnknownAsdu` (with the full raw frame) on `UnknownAsduReceived`, which is
the intended path for reverse-engineering a device's private protocol. All
frames — including link-layer fixed frames — are observable via
`RawFrameReceived` / `FrameTraced` for field diagnostics.


## Getting started (IEC 60870-5-104)

### Client — interrogate a station

```csharp
using IEC60870.CS104;
using IEC60870.Core;

var client = new Iec104Client("127.0.0.1");

client.AsduReceived += (in AsduView asdu) =>
{
    Console.WriteLine($"RX TypeID={(int)asdu.TypeId} COT={asdu.Cot} CA={asdu.Ca}");
    Console.WriteLine(asdu.Raw.ToArray().ToHex());   // built-in debug helper
};

await client.ConnectAsync();                          // sends STARTDT when Autostart=true (default)
await client.SendInterrogationCommandAsync(CauseOfTransmission.ACTIVATION, ca: 1, qoi: 20);

// ... receive & handle responses via client.AsduReceived ...

await client.DisconnectAsync();
```

### Server — spontaneous (unsolicited) push

```csharp
using IEC60870.CS104;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;

var server = new Iec104Server(new APCIParameters(), new ApplicationLayerParameters());

server.ConnectionEvent += (session, ev) =>
{
    if (ev == ApduConnectionEvent.Activated)
        _ = Task.Run(() => PushAsync(server));
};

server.AsduReceived += (session, asdu) =>
{
    // handle commands coming from the master
};

static async Task PushAsync(Iec104Server server)
{
    var al = server.Parameters;
    var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS,
        isTest: false, isNegative: false, oa: 0, ca: 1, isSequence: false);

    asdu.AddInformationObject(new MeasuredValueNormalized(1, 0.5f, new QualityDescriptor()));

    await server.BroadcastAsync(asdu);
}
```

See the [`examples/`](examples) folder for complete, runnable demos
(balanced / unbalanced 101, 104 client / server, TLS, file transfer, and the
Xuji 166/168 private-type sample).

## Project structure

```
IEC60870.Core/        transport-agnostic protocol core (net6.0)
IEC60870.CS101/       IEC 60870-5-101 link layer (net6.0)
IEC60870.CS103/       IEC 60870-5-103 stack (net6.0) — FT1.2 over serial & TCP
IEC60870.CS104/       IEC 60870-5-104 transport (net6.0)
examples/             runnable client / server / sample projects
tests-cs104/          NUnit end-to-end tests for CS101 / CS103 / CS104
```

## Building

```bash
dotnet build IEC60870.sln -c Release
```

## Packaging & publishing

Packages are produced with `dotnet pack` and published to NuGet.org by the
GitHub Actions workflow on version tags (`v*.*.*`). To publish manually:

```bash
dotnet pack IEC60870.Core/IEC60870.Core.csproj   -c Release -p:Version=6.1.0 -o artifacts
dotnet pack IEC60870.CS101/IEC60870.CS101.csproj -c Release -p:Version=6.1.0 -o artifacts
dotnet pack IEC60870.CS103/IEC60870.CS103.csproj -c Release -p:Version=6.1.0 -o artifacts
dotnet pack IEC60870.CS104/IEC60870.CS104.csproj -c Release -p:Version=6.1.0 -o artifacts
dotnet nuget push "artifacts/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key <YOUR_API_KEY>
```

> `Core` must be published before `CS101` / `CS104` (they depend on it).
> `CS103` is independent and can be published in any order.

## License

Released under the [MIT License](LICENSE).

## Contact

LFDCC — 1584329729@qq.com
