﻿using Mirai_CSharp.Exceptions;
using Mirai_CSharp.Helpers;
using Mirai_CSharp.Models;
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mirai_CSharp
{
    public partial class MiraiHttpSession
    {
        /// <summary>
        /// 异步连接到mirai-api-http。
        /// </summary>
        /// <remarks>
        /// 不会连接到指令监控的ws服务。此方法不是线程安全的。
        /// </remarks>
        /// <exception cref="BotNotFoundException"/>
        /// <exception cref="InvalidAuthKeyException"/>
        /// <param name="options">连接信息</param>
        /// <param name="qqNumber">Session将要绑定的Bot的qq号</param>
        public Task ConnectAsync(MiraiHttpSessionOptions options, long qqNumber)
        {
            return ConnectAsync(options, qqNumber, false);
        }
        /// <summary>
        /// 异步连接到mirai-api-http。
        /// </summary>
        /// <remarks>
        /// 此方法不是线程安全的。
        /// </remarks>
        /// <exception cref="BotNotFoundException"/>
        /// <exception cref="InvalidAuthKeyException"/>
        /// <param name="options">连接信息</param>
        /// <param name="qqNumber">Session将要绑定的Bot的qq号</param>
        /// <param name="listenCommand">是否监听指令相关的消息</param>
        public async Task ConnectAsync(MiraiHttpSessionOptions options, long qqNumber, bool listenCommand)
        {
            CheckDisposed();
            if (this.Connected)
            {
                return;
            }
            string sessionKey = await AuthorizeAsync(options);
            await VerifyAsync(options, sessionKey, qqNumber);
            ApiVersion = await GetVersionAsync(options);
            InternalSessionInfo session = new InternalSessionInfo
            {
                Options = options,
                SessionKey = sessionKey,
                QQNumber = qqNumber,
                Canceller = new CancellationTokenSource()
            };
            try
            {
                IMiraiSessionConfig config = await this.GetConfigAsync(session);
                if (!config.EnableWebSocket.GetValueOrDefault())
                {
                    await this.SetConfigAsync(session, new MiraiSessionConfig { CacheSize = config.CacheSize, EnableWebSocket = true });
                }
                CancellationToken token = session.Canceller.Token;
                ReceiveMessageLoop(session, token);
                if (listenCommand)
                {
                    ReceiveCommandLoop(session, token);
                }
                SessionInfo = session;
            }
            catch // 如果这都报错那真是见了鬼了
            {
                _ = InternalReleaseAsync(session);
                throw;
            }
        }

        private static async Task<string> AuthorizeAsync(MiraiHttpSessionOptions options)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { authKey = options.AuthKey });
            using JsonDocument j = await HttpHelper.HttpPostAsync($"{options.BaseUrl}/auth", payload).GetJsonAsync();
            JsonElement root = j.RootElement;
            int code = root.GetProperty("code").GetInt32();
            return code switch
            {
                0 => root.GetProperty("session").GetString(),
                _ => ThrowCommonException<string>(code, in root)
            };
        }

        private static Task VerifyAsync(MiraiHttpSessionOptions options, string sessionKey, long qqNumber)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sessionKey,
                qq = qqNumber
            });
            return InternalHttpPostAsync($"{options.BaseUrl}/verify", payload);
        }
        /// <summary>
        /// 异步获取mirai-api-http的版本号
        /// </summary>
        /// <param name="options">连接信息</param>
        /// <returns></returns>
        public static async Task<Version> GetVersionAsync(MiraiHttpSessionOptions options)
        {
            using JsonDocument j = await HttpHelper.HttpGetAsync($"{options.BaseUrl}/about").GetJsonAsync();
            JsonElement root = j.RootElement;
            int code = root.GetProperty("code").GetInt32();
            if (code == 0)
            {
                return Version.Parse(root.GetProperty("data").GetProperty("version").GetString()[1..]); // v1.0.0, skip 'v'
            }
            return ThrowCommonException<Version>(code, in root);
        }
        /// <summary>
        /// 异步释放Session
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public Task ReleaseAsync(CancellationToken token = default)
        {
            CheckDisposed();
            InternalSessionInfo session = Interlocked.Exchange(ref SessionInfo, null);
            if (session == null)
            {
                throw new InvalidOperationException("请先连接到一个Session。");
            }
            return InternalReleaseAsync(session, token);
        }

        private Task InternalReleaseAsync(InternalSessionInfo session, CancellationToken token = default)
        {
            Plugins = null;
            foreach (FieldInfo eventField in typeof(MiraiHttpSession).GetEvents().Select(p => typeof(MiraiHttpSession).GetField(p.Name, BindingFlags.NonPublic | BindingFlags.Instance)))
            {
                eventField.SetValue(this, null); // 用反射解决掉所有事件的Handler
            }
            session.Canceller.Cancel();
            session.Canceller.Dispose();
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                sessionKey = session.SessionKey,
                qq = session.QQNumber
            });
            return InternalHttpPostAsync($"{session.Options.BaseUrl}/release", payload, token: token);
        }
    }
}