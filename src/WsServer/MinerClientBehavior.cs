﻿using NTMiner.Core;
using NTMiner.Core.Impl;
using NTMiner.Core.MinerServer;
using NTMiner.User;
using NTMiner.Ws;
using System;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace NTMiner {
    public class MinerClientBehavior : WebSocketBehavior {
        private const string _behaviorName = nameof(MinerClientBehavior);

        public MinerClientBehavior() {
            this.EmitOnPing = true;
        }

        protected override void OnOpen() {
            base.OnOpen();
            if (!this.TryGetUser(out WsUserName wsUserName, out UserData userData)) {
                this.CloseAsync();
                return;
            }
            IMinerClientSession minerSession = MinerClientSession.Create(userData, wsUserName, this.ID, Sessions);
            WsRoot.MinerClientSessionSet.Add(minerSession);
            WsRoot.MinerClientMqSender.SendMinerClientWsOpened(minerSession.LoginName, minerSession.ClientId);
            bool isMinerSignChanged;
            if (!WsRoot.MinerSignSet.TryGetByClientId(wsUserName.ClientId, out MinerSign minerSign)) {
                // 此时该矿机是第一次在服务端出现
                minerSign = new MinerSign {
                    Id = LiteDB.ObjectId.NewObjectId().ToString(),
                    ClientId = wsUserName.ClientId,
                    LoginName = userData.LoginName,
                    OuterUserId = wsUserName.UserId,
                    AESPassword = string.Empty,
                    AESPasswordOn = Timestamp.UnixBaseTime
                };
                isMinerSignChanged = true;
            }
            else {
                isMinerSignChanged = minerSign.OuterUserId != wsUserName.UserId || minerSign.LoginName != userData.LoginName;
                if (isMinerSignChanged) {
                    minerSign.OuterUserId = wsUserName.UserId;
                    minerSign.LoginName = userData.LoginName;
                }
            }
            // 通常执行不到，因为用户注册的时候已经生成了RSA公私钥对了
            if (string.IsNullOrEmpty(userData.PublicKey) || string.IsNullOrEmpty(userData.PrivateKey)) {
                var key = Cryptography.RSAUtil.GetRASKey();
                userData.PublicKey = key.PublicKey;
                userData.PrivateKey = key.PrivateKey;
                WsRoot.UserMqSender.SendUpdateUserRSAKey(userData.LoginName, key);
            }
            DateTime now = DateTime.Now;
            if (string.IsNullOrEmpty(minerSign.AESPassword) || minerSign.AESPasswordOn.AddDays(1) < now) {
                isMinerSignChanged = true;
                minerSign.AESPassword = Cryptography.AESUtil.GetRandomPassword();
                minerSign.AESPasswordOn = now;
            }
            base.SendAsync(new WsMessage(Guid.NewGuid(), WsMessage.UpdateAESPassword) {
                Data = new AESPassword {
                    PublicKey = userData.PublicKey,
                    Password = Cryptography.RSAUtil.EncryptString(minerSign.AESPassword, userData.PrivateKey)
                }
            }.SignToJson(minerSign.AESPassword), completed: null);
            if (isMinerSignChanged) {
                WsRoot.MinerClientMqSender.SendChangeMinerSign(minerSign);
            }
        }

        protected override void OnClose(CloseEventArgs e) {
            base.OnClose(e);
            IMinerClientSession minerSession = WsRoot.MinerClientSessionSet.RemoveByWsSessionId(base.ID);
            if (minerSession != null) {
                WsRoot.MinerClientMqSender.SendMinerClientWsClosed(minerSession.LoginName, minerSession.ClientId);
            }
        }

        protected override void OnMessage(MessageEventArgs e) {
            IMinerClientSession minerSession;
            if (e.IsPing) {
                if (WsRoot.MinerClientSessionSet.ActiveByWsSessionId(base.ID, out minerSession)) {
                    WsRoot.MinerClientMqSender.SendMinerClientWsBreathed(minerSession.LoginName, minerSession.ClientId);
                }
                return;
            }
            WsMessage message = e.ToWsMessage<WsMessage>();
            if (message == null) {
                return;
            }
            if (!WsRoot.MinerClientSessionSet.TryGetByWsSessionId(this.ID, out minerSession)) {
                this.CloseAsync(CloseStatusCode.Normal, "意外，会话不存在，请重新连接");
                return;
            }
            else if (MinerClientWsMessageHandler.TryGetHandler(message.Type, out Action<MinerClientBehavior, string, Guid, WsMessage> handler)) {
                try {
                    handler.Invoke(this, minerSession.LoginName, minerSession.ClientId, message);
                }
                catch (Exception ex) {
                    Logger.ErrorDebugLine(ex);
                }
            }
            else {
                NTMinerConsole.UserWarn($"{_behaviorName} {nameof(OnMessage)} Received InvalidType {e.Data}");
            }
        }
    }
}
