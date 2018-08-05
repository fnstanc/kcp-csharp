using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KcpSharp {

    public delegate void KcpEventCallback(UInt32 conv, KcpEvent ev, byte[] msg, string reason);

    public static class KcpConst {
        public const uint KCP_MIN_CONV = 1000;
        public const uint KCP_SESSION_TIME_OUT = 10000;
        public const uint KCP_CONNECT_TIMEOUT = 5000;
        public const uint KCP_CONNECT_REQ_INTERVAL = 500;
        public const uint KCP_HEARTBEAT_INTERVAL = 8000;
    }

    public enum KcpEvent {
        KCP_EV_CONNECT,
        KCP_EV_DISCONNECT,
        KCP_EV_CONNFAILED,
        KCP_EV_MSG,
    }

    public enum KcpCmd {
        KCP_CMD_CONNECT_REQ,
        KCP_CMD_CONNECT_ACK,
        KCP_CMD_DISCONNECT,
        KCP_CMD_HEARTBEAT,
        KCP_CMD_COUNT,
    };

    public class KcpCommand {
        public KcpCmd cmd;
        public uint conv;

        public int Encode(byte[] buf, int size) {
            if (size < 8)
                return -1;

            KCP.ikcp_encode32u(buf, 0, (uint)cmd);
            if (cmd == KcpCmd.KCP_CMD_CONNECT_REQ)
                return 4;
            KCP.ikcp_encode32u(buf, 4, conv);
            return 8;
        }

        public int Decode(byte[] buf, int size) {
            uint n = 0;
            KCP.ikcp_decode32u(buf, 0, ref n);
            if (n >= (uint)KcpCmd.KCP_CMD_COUNT)
                return -1;
            cmd = (KcpCmd)n;
            if (cmd != KcpCmd.KCP_CMD_CONNECT_REQ)
                KCP.ikcp_decode32u(buf, 4, ref conv);
            return 0;
        }
    }

    public struct KcpConfig {
        public int nodelay;
        public int interval;
        public int resend;
        public int nc;
        public int mtu;
        public int snd_wnd;
        public int rcv_wnd;

        /*
        public static KcpConfig Normal() {

        }

        public static KcpConfig Fast() {

        }
        */
    };

    public static class KcpUtils {
        private static readonly DateTime utc_time = new DateTime(1970, 1, 1);
        public static UInt32 Clock32()
        {
            return (UInt32)(Convert.ToInt64(DateTime.UtcNow.Subtract(utc_time).TotalMilliseconds) & 0xffffffff);
        }
    }
}
