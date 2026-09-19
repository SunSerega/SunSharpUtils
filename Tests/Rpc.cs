using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils;
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

        var errors = new List<Exception>();
        Err.Init(new()
        {
            Handle = errors.Add,
        });

        Boolean received_method1 = false;
        await TestMethod(
            send: () =>
            {
                ExampleRpcApi.Method1(123);
            },
            OnMethod1: (x, _) =>
            {
                Assert.AreEqual(123, x);
                received_method1 = true;
            },
            OnMethod2: (_) =>
                throw new InvalidOperationException("Method2 should not be called here"),
            OnMethodStreamed1: (_, _) =>
                throw new InvalidOperationException("MethodStreamed1 should not be called here"),
            OnMethodStreamed2: (_) =>
                throw new InvalidOperationException("MethodStreamed2 should not be called here")
        );
        Assert.IsTrue(received_method1, $"Method1 did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of Method1", ex);

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
            },
            OnMethodStreamed1: (_, _) =>
                throw new InvalidOperationException("MethodStreamed1 should not be called here"),
            OnMethodStreamed2: (_) =>
                throw new InvalidOperationException("MethodStreamed2 should not be called here")
        );
        Assert.IsTrue(received_method2, $"Method2 did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of Method2", ex);

        Boolean received_method_streamed1 = false;
        {
            var len_existing = 10;
            var len = len_existing + 124;
            var received = new List<Int32>();
            await TestMethod(
                send: () =>
                {
                    var wh = new System.Threading.ManualResetEventSlim();
                    ExampleRpcApi.MethodStreamed1(len, (stream) =>
                    {
                        while (stream.TryGetNext(out var item))
                            received.Add(item);
                        wh.Set();
                    });
                    Assert.IsTrue(wh.Wait(TimeSpan.FromSeconds(5)), $"MethodStreamed1 callback was not invoked");
                    Assert.IsTrue(received.SequenceEqual(Enumerable.Range(0, len)), "MethodStreamed1 received sequence does not match expected");
                },
                OnMethod1: (x, _) =>
                    throw new InvalidOperationException("Method1 should not be called here"),
                OnMethod2: (_) =>
                    throw new InvalidOperationException("Method2 should not be called here"),
                OnMethodStreamed1: (input, cancel_token) =>
                {
                    Assert.AreEqual(len, input);
                    var source = new RpcEnumerableSource<Int32>();
                    var i = 0;
                    for (; i < len_existing; i++)
                        source.Push(i);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        for (; i < len; i++)
                            source.Push(i);
                        source.Close();
                    }, cancel_token);
                    received_method_streamed1 = true;
                    return source;
                },
                OnMethodStreamed2: (_) =>
                    throw new InvalidOperationException("MethodStreamed2 should not be called here")
            );
        }
        Assert.IsTrue(received_method_streamed1, $"MethodStreamed1 did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of MethodStreamed1", ex);

        Boolean received_method_streamed2 = false;
        {
            var len_existing = 10;
            var len = len_existing + 124;
            var received_x = -1;
            var received = new List<Int32>();
            await TestMethod(
                send: () =>
                {
                    var wh = new System.Threading.ManualResetEventSlim();
                    ExampleRpcApi.MethodStreamed2((x, stream) =>
                    {
                        received_x = x;
                        while (stream.TryGetNext(out var item))
                            received.Add(item);
                        wh.Set();
                    });
                    Assert.IsTrue(wh.Wait(TimeSpan.FromSeconds(5)), $"MethodStreamed2 callback was not invoked");
                    Assert.AreEqual(len, received_x);
                    Assert.IsTrue(received.SequenceEqual(Enumerable.Range(0, len)), "MethodStreamed2 received sequence does not match expected");
                },
                OnMethod1: (x, _) =>
                    throw new InvalidOperationException("Method1 should not be called here"),
                OnMethod2: (_) =>
                    throw new InvalidOperationException("Method2 should not be called here"),
                OnMethodStreamed1: (_, _) =>
                    throw new InvalidOperationException("MethodStreamed1 should not be called here"),
                OnMethodStreamed2: (cancel_token) =>
                {
                    var source = new RpcEnumerableSource<Int32>();
                    var i = 0;
                    for (; i < len_existing; i++)
                        source.Push(i);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        for (; i < len; i++)
                            source.Push(i);
                        source.Close();
                    }, cancel_token);
                    received_method_streamed2 = true;
                    return (len, source);
                }
            );
        }
        Assert.IsTrue(received_method_streamed2, $"MethodStreamed2 did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of MethodStreamed2", ex);

        async Task TestMethod(Action send, ExampleRpcApi.ProcessClientConfig.Method1Handler OnMethod1, ExampleRpcApi.ProcessClientConfig.Method2Handler OnMethod2, ExampleRpcApi.ProcessClientConfig.MethodStreamed1Handler OnMethodStreamed1, ExampleRpcApi.ProcessClientConfig.MethodStreamed2Handler OnMethodStreamed2)
        {
            var send_task = Task.Run(send);
            var client_socket = listener.Accept();
            ExampleRpcApi.ProcessClient(new()
            {
                Socket = client_socket,
                CancelToken = default,
                OnMethod1 = OnMethod1,
                OnMethod2 = OnMethod2,
                OnMethodStreamed1 = OnMethodStreamed1,
                OnMethodStreamed2 = OnMethodStreamed2,
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

    [RpcApi]
    public static partial void MethodStreamed1(Int32 input, Action<RpcEnumerable<Int32>> on_connected);

    [RpcApi]
    public static partial void MethodStreamed2(Action<Int32, RpcEnumerable<Int32>> on_connected);

}
