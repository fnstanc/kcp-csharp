using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using KcpSharp;

class Program
{
    static void test_v1(string host, UInt16 port)
    {
        bool stop = false;

        KcpSharp.v1.KcpClient client = null;

        client = new KcpSharp.v1.KcpClient((uint conv, KcpSharp.KcpEvent ev, byte[] buf, string err) =>
        {
            switch (ev)
            { 
                case KcpEvent.KCP_EV_CONNECT:
                    Console.WriteLine("connected.");
                    client.Send("Hello KCP.");
                    break;
                case KcpEvent.KCP_EV_CONNFAILED:
                    Console.WriteLine("connect failed. {0}", err);
                    break;
                case KcpEvent.KCP_EV_DISCONNECT:
                    Console.WriteLine("disconnect. {0}", err);
                    break;
                case KcpEvent.KCP_EV_MSG:
                    Console.WriteLine("recv message: {0}", System.Text.ASCIIEncoding.ASCII.GetString(buf) );
                    client.Send(buf);
                    break;
            }
        });

        client.Connect(host, port);

        while (!stop)
        {
            client.Update();
            System.Threading.Thread.Sleep(10);
        }
    }

    static void Main(string[] args)
    {
       // 测试v1版本，有握手过程，服务器决定conv的分配
       test_v1("127.0.0.1", 4000);
    }
}

