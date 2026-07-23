/*
 *  Iec104Server.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;
using IEC60870.Core;



namespace IEC60870.CS104
{
    /// <summary>服务端收到 ASDU 的零拷贝回调（带来源会话）。</summary>
    public delegate void ServerAsduHandler(Iec104Session session, in IEC60870.Core.AsduView asdu);

    /// <summary>
    /// IEC 60870-5-104 异步服务端（从站）。基于 TouchSocket <see cref="TcpService{TClient}"/>，
    /// 每个连接由 <see cref="Iec104Session"/> 承载独立的异步状态机。
    /// </summary>
    /// <remarks>
    /// 用法：
    /// <code>
    /// var server = new Iec104Server();
    /// server.AsduReceived += (Iec104Session s, in AsduView a) => { /* 处理 */ };
    /// await server.StartAsync(2404);
    /// </code>
    /// </remarks>
    public sealed class Iec104Server : TcpService<Iec104Session>
    {
        private readonly APCIParameters _apci;
        private readonly ApplicationLayerParameters _al;
        private readonly ServiceSslOption _sslOption;
        private readonly ConcurrentDictionary<string, Iec104Session> _sessions
            = new ConcurrentDictionary<string, Iec104Session>();

        /// <summary>APCI 参数（k/w/T1/T2/T3）。</summary>
        public APCIParameters ApciParameters => _apci;

        /// <summary>应用层参数（COT/CA/IOA 宽度）。</summary>
        public ApplicationLayerParameters Parameters => _al;

        /// <summary>收到 ASDU 的零拷贝事件（带来源会话，支持多订阅者）。</summary>
        public event ServerAsduHandler AsduReceived;

        /// <summary>连接层事件（带来源会话，支持多订阅者）。</summary>
        public event Action<Iec104Session, ApduConnectionEvent> ConnectionEvent;

        /// <summary>当前活动会话数。</summary>
        public int SessionCount => _sessions.Count;

        public Iec104Server(APCIParameters apciParameters = null,
            ApplicationLayerParameters alParameters = null,
            ServiceSslOption sslOption = null)
        {
            _apci = apciParameters ?? new APCIParameters();
            _al = alParameters ?? new ApplicationLayerParameters();
            _sslOption = sslOption;
        }

        /// <summary>工厂方法：每个新连接创建一个会话实例。</summary>
        protected override Iec104Session NewClient() => new Iec104Session();

        /// <summary>在指定端口启动监听。</summary>
        public async Task StartAsync(int port)
        {
            var config = new TouchSocketConfig();
            config.SetListenIPHosts(new IPHost(port));
            if (_sslOption != null)
                config.SetServiceSslOption(_sslOption);

            await SetupAsync(config).ConfigureAwait(false);
            await StartAsync().ConfigureAwait(false);
        }

        /// <summary>向所有已激活会话广播一个 ASDU。</summary>
        public async Task BroadcastAsync(ASDU asdu, CancellationToken cancellationToken = default)
        {
            foreach (KeyValuePair<string, Iec104Session> kv in _sessions)
            {
                Iec104Session s = kv.Value;
                if (s.IsActivated)
                {
                    try { await s.SendAsync(asdu, cancellationToken).ConfigureAwait(false); }
                    catch { /* 单会话失败不影响其余 */ }
                }
            }
        }

        // ── 内部：会话与回调桥接 ──────────────────────────────────────

        internal void RegisterSession(Iec104Session session) => _sessions[session.Id] = session;

        internal void UnregisterSession(Iec104Session session) => _sessions.TryRemove(session.Id, out _);

        internal AsduViewHandler RaiseAsduReceived(Iec104Session session)
            => (in AsduView a) => AsduReceived?.Invoke(session, in a);

        internal void RaiseConnectionEvent(Iec104Session session, ApduConnectionEvent ev)
            => ConnectionEvent?.Invoke(session, ev);
    }
}
