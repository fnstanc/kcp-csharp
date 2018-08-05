using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KcpSharp.v1 {
    public class KcpClient
    {
        enum Status {
            None,
            Connecting,
            Connected,
            Disconnect,
            Disconnecting,
            Timemout,
        }
        private UdpClient udpClient_;
        private IPEndPoint ep_;
        private IPEndPoint serverEp_;
        private KcpEventCallback eventCallback_;
        private KCP kcp_;
        private bool needUpdate_;
        private UInt32 nextUpdateTime_;

        private Status status_ = Status.None;
        private UInt32 connectStartTime_;
        private UInt32 lastConnectReqTime_;

        private SwitchQueue<byte[]> recvQueue_ = new SwitchQueue<byte[]>(128);

        private KcpCommand kcpCommand_ = new KcpCommand();
        private byte[] cmdBuf_ = new byte[8];
        private UInt32 conv_ = 0;

        public KcpClient(KcpEventCallback handler)
        {
            eventCallback_ = handler;
        }

        public void Connect(string host, UInt16 port)
        {
            Clean();
            serverEp_ = new IPEndPoint(IPAddress.Parse(host), port);
            udpClient_ = new UdpClient(host, port);
            udpClient_.Connect(serverEp_);
            status_ = Status.Connecting;
            connectStartTime_ = KcpUtils.Clock32();
            udpClient_.BeginReceive(ReceiveCallback, this);
        }

        void ReceiveCallback(IAsyncResult ar)
        {
            if (udpClient_ == null)
                return;

            byte[] data = udpClient_.EndReceive(ar, ref ep_);
            if (null != data)
                OnRecv(data);

            if (udpClient_ != null)
            {
                // try to receive again.
                udpClient_.BeginReceive(ReceiveCallback, this);
            }
        }

        void OnRecv(byte[] buf)
        {
            recvQueue_.Push(buf);
        }

        void Clean()
        {
            status_ = Status.None;
            conv_ = 0;

            needUpdate_ = false;
            nextUpdateTime_ = 0;

            connectStartTime_ = 0;
            lastConnectReqTime_ = 0;
            recvQueue_.Clear();
            kcp_ = null;

            if (udpClient_ != null) {
                udpClient_.Close();
                udpClient_ = null;
            }

            ep_ = null;
            serverEp_ = null;
        }

        void InitKcp(UInt32 conv)
        {
            conv_ = conv;
            kcp_ = new KCP(conv, (byte[] buf, int size) =>
            {
                SendUdpPacket(buf, size);
            });

            kcp_.NoDelay(1, 10, 2, 1);
        }

        public void Send(byte[] buf)
        {
            if (status_ != Status.Connected)
                return;
            kcp_.Send(buf);
            needUpdate_ = true;
        }

        public void Send(string str)
        {
            Send(System.Text.ASCIIEncoding.ASCII.GetBytes(str));
        }

        public void Update()
        {
            uint current = KcpUtils.Clock32();

            switch(status_) {
                case Status.Connecting: {
                    HandleConnectAck();
                    if (ConnectTimeout(current))
                    {

                        Clean();
                        status_ = Status.Timemout;
                        eventCallback_(0, KcpEvent.KCP_EV_CONNFAILED, null, "Timeout");
                        return;
                    }
                    if (NeedSendConnectReq(current))
                    {
                        lastConnectReqTime_ = current;
                        kcpCommand_.cmd = KcpCmd.KCP_CMD_CONNECT_REQ;
                        SendKcpCommand(kcpCommand_);
                    }
                } break;
                case Status.Connected: {
                    ProcessRecvQueue();

                    if (needUpdate_ || current >= nextUpdateTime_)
                    {
                        kcp_.Update(current);
                        nextUpdateTime_ = kcp_.Check(current);
                        needUpdate_ = false;
                    }
                } break;
                case Status.Disconnecting: {

                }
                break;
            }
        }

        public void Close()
        {
            if (status_ == Status.Connected) {
                status_ = Status.Disconnecting;
                kcpCommand_.cmd = KcpCmd.KCP_CMD_DISCONNECT;
                kcpCommand_.conv = conv_;
                SendKcpCommand(kcpCommand_);
            } else {
                OnDisconnect();
            }
        }

        public void OnDisconnect() {
            if (status_ != Status.Disconnect) {
                status_ = Status.Disconnect;
                uint conv = conv_;
                Clean();
                status_ = Status.Disconnect;
                eventCallback_(conv, KcpEvent.KCP_EV_DISCONNECT, null, "Closed");
            }
        }

        private void SendUdpPacket(byte[] buf, int size) {
            if (udpClient_ != null)
                udpClient_.Send(buf, size);
        }

        void HandleConnectAck()
        {
            if (recvQueue_.Empty())
                recvQueue_.Switch();

            while (status_ == Status.Connecting && !recvQueue_.Empty())
            {
                var buf = recvQueue_.Pop();
                if (kcpCommand_.Decode(buf, buf.Length) < 0)
                    return;

                if (kcpCommand_.cmd == KcpCmd.KCP_CMD_CONNECT_ACK) {
                    InitKcp(kcpCommand_.conv);
                    status_ = Status.Connected;
                    eventCallback_(conv_, KcpEvent.KCP_EV_CONNECT, null, null);
                    break;
                }
            }
        }

        void ProcessRecvQueue()
        {
            if (recvQueue_.Empty())
                recvQueue_.Switch();

            while (status_ == Status.Connected && !recvQueue_.Empty())
            {
                var buf = recvQueue_.Pop();

                UInt32 conv = 0;
                if (KCP.ikcp_decode32u(buf, 0, ref conv) < 0) {
                    // handle kcp command
                    continue;
                }

                if (conv < KcpConst.KCP_MIN_CONV) {
                    if (kcpCommand_.Decode(buf, buf.Length) < 0)
                        continue;

                    if (conv_ != kcpCommand_.conv)
                        continue;

                    switch(kcpCommand_.cmd) {
                        case KcpCmd.KCP_CMD_DISCONNECT: {
                            OnDisconnect();
                        }
                        break;
                        case KcpCmd.KCP_CMD_HEARTBEAT: {

                        }
                        break;
                    }
                } else {
                    kcp_.Input(buf);
                    needUpdate_ = true;

                    for (var size = kcp_.PeekSize(); size > 0; size = kcp_.PeekSize())
                    {
                        var buffer = new byte[size];
                        if (kcp_.Recv(buffer) > 0)
                        {
                            eventCallback_(conv, KcpEvent.KCP_EV_MSG, buffer, null);
                        }
                    }
                }

            }
        }

        bool ConnectTimeout(UInt32 current)
        {
            return current - connectStartTime_ > KcpConst.KCP_CONNECT_TIMEOUT;
        }

        bool NeedSendConnectReq(UInt32 current)
        {
            return current - lastConnectReqTime_ > KcpConst.KCP_CONNECT_REQ_INTERVAL;
        }

        private void SendKcpCommand(KcpCommand cmd) {
            int sz = cmd.Encode(cmdBuf_, cmdBuf_.Length);
            if (sz > 0)
                SendUdpPacket(cmdBuf_, sz);
        }
    }

}