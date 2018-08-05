using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KcpSharp.v1 {
    // 客户端主动发送握手数据
    // 服务器下发conv
    public class KcpClient
    {

        private UdpClient mUdpClient;
        private IPEndPoint mIPEndPoint;
        private IPEndPoint mSvrEndPoint;
        private KcpEventCallback evHandler;
        private KCP mKcp;
        private bool mNeedUpdateFlag;
        private UInt32 mNextUpdateTime;

        private bool mInConnectStage;
        private bool mConnectSucceed;
        private UInt32 mConnectStartTime;
        private UInt32 mLastSendConnectTime;

        private SwitchQueue<byte[]> mRecvQueue = new SwitchQueue<byte[]>(128);

        private KcpCommand kcpCommand_ = new KcpCommand();
        private byte[] cmdBuf_ = new byte[8];
        private UInt32 conv_ = 0;

        public KcpClient(KcpEventCallback handler)
        {
            evHandler = handler;
        }

        public void Connect(string host, UInt16 port)
        {
            mSvrEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
            mUdpClient = new UdpClient(host, port);
            mUdpClient.Connect(mSvrEndPoint);

            ResetState();

            mInConnectStage = true;
            mConnectStartTime = KcpUtils.Clock32();

            mUdpClient.BeginReceive(ReceiveCallback, this);
        }

        void ReceiveCallback(IAsyncResult ar)
        {
            byte[] data = mUdpClient.EndReceive(ar, ref mIPEndPoint);
            if (null != data)
                OnRecv(data);

            if (mUdpClient != null)
            {
                // try to receive again.
                mUdpClient.BeginReceive(ReceiveCallback, this);
            }
        }

        void OnRecv(byte[] buf)
        {
            mRecvQueue.Push(buf);
        }

        void ResetState()
        {
            mNeedUpdateFlag = false;
            mNextUpdateTime = 0;

            mInConnectStage = false;
            mConnectSucceed = false;
            mConnectStartTime = 0;
            mLastSendConnectTime = 0;
            mRecvQueue.Clear();
            conv_ = 0;
            mKcp = null;
        }

        void InitKcp(UInt32 conv)
        {
            conv_ = conv;
            mKcp = new KCP(conv, (byte[] buf, int size) =>
            {
                mUdpClient.Send(buf, size);
            });

            mKcp.NoDelay(1, 10, 2, 1);
        }

        public void Send(byte[] buf)
        {
            mKcp.Send(buf);
            mNeedUpdateFlag = true;
        }

        public void Send(string str)
        {
            Send(System.Text.ASCIIEncoding.ASCII.GetBytes(str));
        }

        public void Update()
        {
            uint current = KcpUtils.Clock32();
            if (mInConnectStage)
            {
                HandleConnectAck();

                if (ConnectTimeout(current))
                {
                    evHandler(0, KcpEvent.KCP_EV_CONNFAILED, null, "Timeout");
                    mInConnectStage = false;
                    return;
                }

                if (NeedSendConnectReq(current))
                {
                    mLastSendConnectTime = current;
                    kcpCommand_.cmd = KcpCmd.KCP_CMD_CONNECT_REQ;
                    SendKcpCommand(kcpCommand_);
                }

                return;
            }

            if (mConnectSucceed)
            {
                ProcessRecvQueue();

                if (mNeedUpdateFlag || current >= mNextUpdateTime)
                {
                    mKcp.Update(current);
                    mNextUpdateTime = mKcp.Check(current);
                    mNeedUpdateFlag = false;
                }
            }
        }

        public void Close()
        {
            mUdpClient.Close();
            evHandler(conv_, KcpEvent.KCP_EV_DISCONNECT, null, "Closed");
        }

        private void SendUdpPacket(byte[] buf, int size) {
            if (mUdpClient != null)
                mUdpClient.Send(buf, size);
        }

        void HandleConnectAck()
        {
            if (mRecvQueue.Empty())
                mRecvQueue.Switch();

            while (mInConnectStage && !mRecvQueue.Empty())
            {
                var buf = mRecvQueue.Pop();
                if (kcpCommand_.Decode(buf, buf.Length) < 0)
                    return;

                if (kcpCommand_.cmd == KcpCmd.KCP_CMD_CONNECT_ACK) {
                    InitKcp(kcpCommand_.conv);
                    mInConnectStage = false;
                    mConnectSucceed = true;
                    evHandler(conv_, KcpEvent.KCP_EV_CONNECT, null, null);
                    break;
                }
            }
        }

        void ProcessRecvQueue()
        {
            if (mRecvQueue.Empty())
                mRecvQueue.Switch();

            while (!mRecvQueue.Empty())
            {
                var buf = mRecvQueue.Pop();

                UInt32 conv = 0;
                if (KCP.ikcp_decode32u(buf, 0, ref conv) < 0) {
                    // handle kcp command
                    continue;
                }

                if (conv < KcpConst.KCP_MIN_CONV) {
                    if (kcpCommand_.Decode(buf, buf.Length) < 0)
                        continue;

                    switch(kcpCommand_.cmd) {
                        case KcpCmd.KCP_CMD_DISCONNECT: {

                        }
                        break;
                        case KcpCmd.KCP_CMD_HEARTBEAT: {

                        }
                        break;
                    }
                } else {
                    mKcp.Input(buf);
                    mNeedUpdateFlag = true;

                    for (var size = mKcp.PeekSize(); size > 0; size = mKcp.PeekSize())
                    {
                        var buffer = new byte[size];
                        if (mKcp.Recv(buffer) > 0)
                        {
                            evHandler(conv, KcpEvent.KCP_EV_MSG, buffer, null);
                        }
                    }
                }

            }
        }

        bool ConnectTimeout(UInt32 current)
        {
            return current - mConnectStartTime > KcpConst.KCP_CONNECT_TIMEOUT;
        }

        bool NeedSendConnectReq(UInt32 current)
        {
            return current - mLastSendConnectTime > KcpConst.KCP_CONNECT_REQ_INTERVAL;
        }

        private void SendKcpCommand(KcpCommand cmd) {
            int sz = cmd.Encode(cmdBuf_, cmdBuf_.Length);
            if (sz > 0)
                SendUdpPacket(cmdBuf_, sz);
        }
    }

}