//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;


namespace IEC60870.CS101;

    /// <summary>
    /// 串口 FT1.2 收发器（异步）。基于 <see cref="SerialPort.BaseStream"/> 的异步读写，
    /// 通过 <see cref="FT12Framer"/> 完成帧定界。实现 <see cref="ISerialLinkTransport"/>。
    /// </summary>
    internal sealed class SerialTransceiverFT12 : ISerialLinkTransport
    {
        private Stream _serialStream = null;
        private SerialPort _port = null;

        private Action<string> _debugLog;
        private LinkLayerParameters _linkLayerParameters;

        // 默认与 lib60870 一致：等待帧起始字符 50ms；字符间超时 50ms。
        // _characterTimeout 约束的是"相邻字节之间的空隙"，不是整帧总时长
        // （见 StreamByteSource.ReadAsync 与 SetTimeouts 的注释）。
        private int _messageTimeout = 50;
        private int _characterTimeout = 50;

        /// <summary>读帧用的字节源；首次收发时按需创建。</summary>
        private StreamByteSource _source = null;

        /// <summary>
        /// 解析（必要时先打开）串口字节流；顺带按需创建流式分帧用的 <see cref="StreamByteSource"/>。
        /// </summary>
        /// <remarks>
        /// 必须延迟到首次收发时解析：<see cref="SerialPort.BaseStream"/> 在端口未打开时读取会抛
        /// <see cref="InvalidOperationException"/>，而串口所有权约定是"调用方传入、由库在
        /// StartAsync 里打开"（见 Iec101Server/Iec101Client.StartAsync）。构造函数里提前取
        /// BaseStream 会让"传一个未打开的 SerialPort"这种标准用法直接崩在构造阶段，
        /// 永远走不到 StartAsync 的 Open。
        /// </remarks>
        private Stream EnsureStream()
        {
            if (_port != null)
            {
                if (_port.IsOpen == false)
                {
                    _port.Open();
                }

                // 端口每次重新 Open 都会给出新的 BaseStream。原先只在首次缓存，
                // 端口关闭再打开后仍指向已释放的旧流 → 读立即失败、写抛异常。
                var stream = _port.BaseStream;
                if (!ReferenceEquals(stream, _serialStream))
                {
                    _serialStream = stream;
                    _source = null;
                }
            }

            return _serialStream;
        }

        /// <summary>读帧用的字节源（与 <see cref="EnsureStream"/> 同生命周期，首次读帧时创建）。</summary>
        private StreamByteSource GetSource()
        {
            EnsureStream();

            return _source ??= new StreamByteSource(_serialStream);
        }
        private EventHandler _portDenied;

        public event EventHandler PortDenied
        {
            add { _portDenied += value; }
            remove { _portDenied -= value; }
        }

        public SerialTransceiverFT12(SerialPort port, LinkLayerParameters linkLayerParameters, Action<string> debugLog)
        {
            _port = port;
            // 不在此处取 BaseStream：端口可能还没打开（见 GetSource 注释）
            _debugLog = debugLog;
            _linkLayerParameters = linkLayerParameters;
        }

        public SerialTransceiverFT12(Stream serialStream, LinkLayerParameters linkLayerParameters, Action<string> debugLog)
        {
            _port = null;
            _serialStream = serialStream;
            _debugLog = debugLog;
            _linkLayerParameters = linkLayerParameters;
            _source = new StreamByteSource(_serialStream);
        }

        public int BaudRate
        {
            get
            {
                if (_port != null)
            {
                return _port.BaudRate;
            }
            else
            {
                return 10000000;
            }
        }
        }

        /// <summary>
        /// 设置接收超时（毫秒）。运行期任意时刻可调用（字段级赋值），对下一次读帧生效。
        /// </summary>
        /// <param name="messageTimeout">等待帧起始字符的最大时长（本轮无数据时的等待窗口）。</param>
        /// <param name="characterTimeout">
        /// <b>字符间超时</b>（character inter-byte timeout）：帧内相邻两个字节之间的最大间隔，
        /// <b>不是整帧的总时长</b>。9600 波特下每字节约 1.04ms，255 字节的帧体传输约 265ms，
        /// 但字节之间只有约 1ms 的空隙，因此该值只负责"断流判定"，
        /// 长帧（总召唤响应、多 IOA 帧）不会被整帧窗口误杀（见 <see cref="StreamByteSource"/>）。
        /// </param>
        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _messageTimeout = messageTimeout;
            _characterTimeout = characterTimeout;
        }

        public async ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var source = GetSource();

            var n = await FT12Framer.ReadFrameAsync(source, buffer, _linkLayerParameters,
                _messageTimeout, _characterTimeout, _debugLog, ct).ConfigureAwait(false);

            if (n > 0)
            {
                _debugLog("RECV " + BitConverter.ToString(buffer.Span.Slice(0, n).ToArray()));
            }
            else if (source.IsFaulted)
            {
                // 串口被拔出/被外部关闭/流已释放：读路径原先完全静默，主循环无延迟空转。
                // 与写路径对称地转成 PortDenied —— 上层会置致命错误并停止循环，运维可见。
                _debugLog("READ FAULTED: serial stream is no longer usable");
                OnPortDenied();
            }

            return n;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _debugLog("SEND " + BitConverter.ToString(data.Span.ToArray()));

            try
            {
                await EnsureStream().WriteAsync(data, ct).ConfigureAwait(false);
                await EnsureStream().FlushAsync(ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                OnPortDenied();
            }
            catch (IOException)
            {
                OnPortDenied();
            }
            catch (ObjectDisposedException)
            {
                OnPortDenied();
            }
        }

        private void OnPortDenied()
        {
            if (_portDenied != null)
            {
                try
            {
                _portDenied(this, EventArgs.Empty);
            }
            catch { }
        }
    }

    public void Dispose()
        {
            // 不在此关闭 _serialStream：串口由 CS101Master/ServerBase 拥有；TCP 隧道由各自传输层管理。
        }
    }
