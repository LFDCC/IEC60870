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
            // 并发向各会话发送，避免某个会话 k 窗口满时串行阻塞其余会话（代码评审 #9）。
            // 每个会话独立编码（参数可能不同），并行安全：SendAsync 内部各自分配独立的 PooledApduWriter。
            var tasks = new List<Task>(_sessions.Count);
            foreach (KeyValuePair<string, Iec104Session> kv in _sessions)
            {
                Iec104Session s = kv.Value;
                if (s.IsActivated)
                    tasks.Add(SendOneAsync(s, asdu, cancellationToken));
            }

            // 并发等待：单会话失败不影响其余会话的发送结果，但汇总所有失败以便调用方排查。
            // SendOneAsync 只透传 OperationCanceledException，其余异常原样抛到此处被 catch。
            List<Exception> failures = null;
            foreach (Task t in tasks)
            {
                try { await t.ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; } // 取消必须上抛
                catch (Exception ex)
                {
                    (failures ??= new List<Exception>()).Add(ex);
                }
            }

            if (failures != null)
                throw new AggregateException("部分会话广播失败", failures);
        }

        private static async Task SendOneAsync(Iec104Session s, ASDU asdu, CancellationToken ct)
        {
            // 不吞 Exception：让异常传到 BroadcastAsync 的 await 处被 catch 汇总到 failures。
            // 仅 OperationCanceledException 透传（取消语义必须上抛）。
            await s.SendAsync(asdu, ct).ConfigureAwait(false);
        }

        // ── 内部：会话与回调桥接 ──────────────────────────────────────

        internal void RegisterSession(Iec104Session session) => _sessions[session.Id] = session;

        internal void UnregisterSession(Iec104Session session) => _sessions.TryRemove(session.Id, out _);

        internal AsduViewHandler RaiseAsduReceived(Iec104Session session)
            => (in AsduView a) => AsduReceived?.Invoke(session, in a);

        internal void RaiseConnectionEvent(Iec104Session session, ApduConnectionEvent ev)
            => ConnectionEvent?.Invoke(session, ev);

        /// <summary>
        /// 停止监听并关闭所有会话。服务端主动停止不会为每个会话派发 ConnectionClosed
        /// （每个会话已在关闭前标记 <see cref="Iec104Session.MarkIntentionalClose"/>，避免刷屏）；
        /// 客户端侧断开、超时或协议错误仍会正常派发，使订阅者感知“哪个会话”断开。
        /// </summary>
        public new async Task StopAsync()
        {
            foreach (Iec104Session s in _sessions.Values)
                s.MarkIntentionalClose();
            await base.StopAsync().ConfigureAwait(false);
        }
    }
}
