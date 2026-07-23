# IEC60870.NET (Async)

![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

IEC 60870-5-101 / 104 protocol library for **.NET 8**, fully refactored for
asynchronous operation on top of [TouchSocket](https://github.com/RRQM/TouchSocket).

This project is an async fork/rebrand of the original
[lib60870.NET](https://github.com/mz-automation/lib60870.net) (MZ Automation).
The entire stack has been rewritten around `Task` / `ValueTask`, `Span<byte>`,
and pooled, zero-allocation buffering, while keeping the familiar
information-object model. It is released under the **MIT License**.

## Features

- **Fully asynchronous** client/server APIs (`ConnectAsync`, `SendAsync`,
  `BroadcastAsync`, …) — no blocking socket calls anywhere on the hot path.
- **Low / zero GC pressure**: received bytes flow
  `ByteBlock → Span → ApduReader / AsduView → callback`; outgoing frames are
  built with a pooled `PooledApduWriter` and returned to the array pool after
  the send completes.
- **Three independent packages** so you only ship what you use:
  `Core` (transport-agnostic protocol), `CS101` (101 link layer),
  `CS104` (104 transport).
- **Vendor private types** via `IPrivateIOFactory` — register custom TypeIDs
  (e.g. Xuji 166/168 fault reports) and let the decoder handle them.
- **Built-in debug helpers**:
  - `byte[].ToHex()` — high-performance hex string (`IEC60870.Core`).
  - `byte[]` / `ASDU` → `ToTelegram()` — renders APCI / ASDU / raw frame text
    for logging (`IEC60870.CS104`).
- **TLS support** for CS104 via TouchSocket's SSL transport.
- Targets `net8.0` only.

## Packages

| Package | Description |
| --- | --- |
| `LFDCC.IEC60870.Core`  | Transport-agnostic protocol core: `ASDU`, information objects, time/quality types, hex & telegram helpers. |
| `LFDCC.IEC60870.CS101` | IEC 60870-5-101 link layer (balanced / unbalanced, serial & TCP) on TouchSocket. |
| `LFDCC.IEC60870.CS104` | IEC 60870-5-104 TCP / TLS transport on TouchSocket. |

```bash
dotnet add package LFDCC.IEC60870.CS104
```

> `CS101` and `CS104` both depend on `LFDCC.IEC60870.Core`; NuGet resolves and
> installs it automatically.

## Supported protocols

| Standard | Transport | Library |
| --- | --- | --- |
| IEC 60870-5-104 | TCP / TLS | `IEC60870.CS104` |
| IEC 60870-5-101 | Serial (FT1.2), balanced & unbalanced, and TCP | `IEC60870.CS101` |

## Getting started (IEC 60870-5-104)

### Client — interrogate a station

```csharp
using IEC60870.CS104;
using IEC60870.Core;

var client = new Iec104Client("127.0.0.1");

client.AsduReceived += (in AsduView asdu) =>
{
    Console.WriteLine($"RX TypeID={(int)asdu.TypeId} COT={asdu.Cot} CA={asdu.CommonAddress}");
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
IEC60870.Core/        transport-agnostic protocol core (net8.0)
IEC60870.CS101/       IEC 60870-5-101 link layer (net8.0)
IEC60870.CS104/       IEC 60870-5-104 transport (net8.0)
examples/             runnable client / server / sample projects
tests-cs104/          NUnit end-to-end tests for CS104
```

## Building

```bash
dotnet build IEC60870.sln -c Release
```

## Packaging & publishing

Packages are produced with `dotnet pack` and published to NuGet.org by the
GitHub Actions workflow on version tags (`v*.*.*`). To publish manually:

```bash
dotnet pack IEC60870.Core/IEC60870.Core.csproj   -c Release -p:Version=4.0.0 -o artifacts
dotnet pack IEC60870.CS101/IEC60870.CS101.csproj -c Release -p:Version=4.0.0 -o artifacts
dotnet pack IEC60870.CS104/IEC60870.CS104.csproj -c Release -p:Version=4.0.0 -o artifacts
dotnet nuget push "artifacts/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key <YOUR_API_KEY>
```

> `Core` must be published together with `CS101` / `CS104` because the latter
> depend on it.

## License

Released under the [MIT License](LICENSE).

## Contact

LFDCC — 1584329729@qq.com
