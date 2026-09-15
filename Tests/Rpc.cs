using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.DataStash;

namespace Tests;

[TestClass]
public class Rpc
{

    [TestMethod]
    public async Task RpcApi()
    {
        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 12345));
        listener.Listen(backlog: 1);
        ExampleRpcApi.ClientConnector.Init(new()
        {
            Host = "localhost",
            Port = 12345,
        });

        Boolean received_method1 = false;
        await TestMethod(
            send: () =>
            {
                ExampleRpcApi.Method1(123);
            },
            OnMethod1: (x, _) =>
            {
                received_method1 = true;
                Assert.AreEqual(123, x);
            },
            OnMethod2: (_) =>
                throw new InvalidOperationException("Method2 should not be called here")
        );
        Assert.IsTrue(received_method1, $"Method1 did not fire");

        Boolean received_method2 = false;
        await TestMethod(
            send: () =>
            {
                var result = ExampleRpcApi.Method2();
                Assert.AreEqual(456, result);
            },
            OnMethod1: (x, _) =>
                throw new InvalidOperationException("Method1 should not be called here"),
            OnMethod2: (_) =>
            {
                received_method2 = true;
                return 456;
            }
        );
        Assert.IsTrue(received_method2, $"Method2 did not fire");

        async Task TestMethod(Action send, ExampleRpcApi.ProcessClientConfig.Method1Handler OnMethod1, ExampleRpcApi.ProcessClientConfig.Method2Handler OnMethod2)
        {
            var send_task = Task.Run(send);
            var client_socket = listener.Accept();
            ExampleRpcApi.ProcessClient(new()
            {
                Socket = client_socket,
                CancelToken = default,
                OnMethod1 = OnMethod1,
                OnMethod2 = OnMethod2,
            });
            await send_task;
        }
    }

}

internal static partial class ExampleRpcApi
{

    [RpcApi]
    public static partial void Method1(Int32 x);

    [RpcApi]
    public static partial Int32 Method2();

}
