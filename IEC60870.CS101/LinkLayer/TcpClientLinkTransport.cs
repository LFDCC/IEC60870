//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;


namespace IEC60870.CS101;

    /// <summary>
    /// TCP 客户端虚拟串口（隧道）。基于 TouchSocket <see cref="TcpClient"/>，将收到的字节推入
    /// <see cref="AsyncByteQueue"/>，由 <see cref="FT12Framer"/> 在其上完成 FT1.2 帧定界。
    /// 无原始 Socket / 工作线程。
    /// </summary>
    internal sealed class TcpClientLinkTransport : TcpClient, ISerialLinkTransport
    {
        private readonly AsyncByteQueue _queue = new AsyncByteQueue();
        private LinkLayerParameters _llParams;
        private Action<string> _log;
        private int _msgTimeout = 50;
        private int _charTimeout = 50;
        private EventHandler _portDenied;
        private readonly string _host;
        private readonly int _port;

        public event EventHandler PortDenied
        {
            add { _portDenied += value; }
            remove { _portDenied -= value; }
        }

        public TcpClientLinkTransport(string host, int port, LinkLayerParameters llParams, Action<string> log)
        {
            _host = host;
            _port = port;
            _llParams = llParams;
            _log = log;
        }

        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _msgTimeout = messageTimeout;
            _charTimeout = characterTimeout;
        }

        protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
        {
            if (!e.Memory.IsEmpty)
        {
            _queue.Write(e.Memory.Span);
        }

        await base.OnTcpReceived(e).ConfigureAwait(false);
        }

        // 以 TcpClientLinkTransport 类型调用时解析到本实现（额外完成 SetupAsync 与建连超时）；
        // 基类-typed 引用调用 ConnectAsync(CancellationToken) 则为纯网络握手。
        public async Task ConnectAsync(CancellationToken ct, Action<TouchSocketConfig> configureConfig = null)
        {
            var config = new TouchSocketConfig();
            config.SetRemoteIPHost(new IPHost($"{_host}:{_port}"));
            // 用户自定义配置在库默认项之后应用
            configureConfig?.Invoke(config);
            await SetupAsync(config).ConfigureAwait(false);
            // TouchSocket 4.x 的 ConnectAsync 不再接收超时参数，用取消令牌保留 1s 建连超时。
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(1000);
            await base.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }

        public async ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var n = await FT12Framer.ReadFrameAsync(_queue, buffer, _llParams, _msgTimeout, _charTimeout, _log, ct)
                .ConfigureAwait(false);

            if (n > 0)
        {
            _log("RECV " + BitConverter.ToString(buffer.Span.Slice(0, n).ToArray()));
        }

        return n;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _log("SEND " + BitConverter.ToString(data.Span.ToArray()));
            try
            {
                await base.SendAsync(data, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _portDenied?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Dispose 钩子：关闭字节队列并沿 TouchSocket 生命周期释放底层 TcpClient。</summary>
        protected override void SafetyDispose(bool disposing)
        {
            _queue.SafeDispose();
            base.SafetyDispose(disposing);
        }
    }
