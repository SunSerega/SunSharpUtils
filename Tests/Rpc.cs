using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils;
using SunSharpUtils.DataStash;

namespace Tests;

[TestClass]
public class Rpc
{

    private readonly struct ProcessClientOptionalConfig()
    {
        public ExampleRpcApi.ProcessClientConfig.Method1Handler OnMethod1 { get; init; } = (_, _) =>
            throw new InvalidOperationException("Method1 should not be called here");
        public ExampleRpcApi.ProcessClientConfig.Method2Handler OnMethod2 { get; init; } = (_) =>
            throw new InvalidOperationException("Method2 should not be called here");
        public ExampleRpcApi.ProcessClientConfig.MethodStreamed1DeferredHandler OnMethodStreamed1Deferred { get; init; } = (_, _) =>
            throw new InvalidOperationException("MethodStreamed1Deferred should not be called here");
        public ExampleRpcApi.ProcessClientConfig.MethodStreamed2DeferredHandler OnMethodStreamed2Deferred { get; init; } = (_) =>
            throw new InvalidOperationException("MethodStreamed2Deferred should not be called here");
        public ExampleRpcApi.ProcessClientConfig.MethodStreamed3BlockingHandler OnMethodStreamed3Blocking { get; init; } = (_, _) =>
            throw new InvalidOperationException("MethodStreamed3Blocking should not be called here");
        public ExampleRpcApi.ProcessClientConfig.Method3ExpectedExceptionHandler OnMethod3ExpectedException { get; init; } = (_) =>
            throw new InvalidOperationException("Method3ExpectedException should not be called here");
    }

    [TestMethod]
    public async Task RpcApi()
    {
        var errors = new List<Exception>();
        Err.Init(new()
        {
            Handle = errors.Add,
        });

        using var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 12345));
        listener.Listen(backlog: 1);
        ExampleRpcApi.ClientConnector.Init(new()
        {
            Host = "localhost",
            Port = 12345,
            CancelToken = default,

            SendTimeout = TimeSpan.FromMinutes(1),
            ReceiveTimeout = TimeSpan.FromMinutes(1),
        });

        async Task TestMethod(Action send, ProcessClientOptionalConfig handlers)
        {
            var send_task = Task.Run(send);
            var client_socket = listener.Accept();
            client_socket.ReceiveTimeout = (Int32)TimeSpan.FromMinutes(1).TotalMilliseconds;
            client_socket.SendTimeout = (Int32)TimeSpan.FromMinutes(1).TotalMilliseconds;
            var client_processor = ExampleRpcApi.CreateClientProcessor(new()
            {
                OnMethod1 = handlers.OnMethod1,
                OnMethod2 = handlers.OnMethod2,
                OnMethodStreamed1Deferred = handlers.OnMethodStreamed1Deferred,
                OnMethodStreamed2Deferred = handlers.OnMethodStreamed2Deferred,
                OnMethodStreamed3Blocking = handlers.OnMethodStreamed3Blocking,
                OnMethod3ExpectedException = handlers.OnMethod3ExpectedException,
            });
            client_processor.ProcessClient(client_socket, default);
            await send_task;
        }

        Boolean received_method1 = false;
        await TestMethod(
            send: () =>
            {
                ExampleRpcApi.Method1(123);
            },
            new()
            {
                OnMethod1 = (x, _) =>
                {
                    Assert.AreEqual(123, x);
                    received_method1 = true;
                },
            }
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
            new()
            {
                OnMethod2 = (_) =>
                {
                    received_method2 = true;
                    return 456;
                },
            }
        );
        Assert.IsTrue(received_method2, $"Method2 did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of Method2", ex);

        Boolean received_method_streamed1 = false;
        {
            var len_existing = 10;
            var len = len_existing + 124;
            await TestMethod(
                send: () =>
                {
                    var received = new List<Int32>();
                    var wh = new ManualResetEventSlim();
                    ExampleRpcApi.MethodStreamed1Deferred(len, async (stream) =>
                    {
                        await foreach (var item in stream.ReadItemsAsync())
                            received.Add(item);
                        wh.Set();
                    }, read_cancel_token: default);
                    Assert.IsTrue(wh.Wait(TimeSpan.FromSeconds(5)), $"MethodStreamed1Deferred callback was not invoked");
                    Assert.IsTrue(received.SequenceEqual(Enumerable.Range(0, len)), "MethodStreamed1Deferred received sequence does not match expected");
                },
                new()
                {
                    OnMethodStreamed1Deferred = (input, cancel_token) =>
                    {
                        Assert.AreEqual(len, input);
                        var source = new RpcEnumerableSource<Int32>(f => f.CreatePermaHolder(), ping_stop_token: default);
                        var i = 0;
                        for (; i < len_existing; i++)
                            source.Push(i);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            for (; i < len; i++)
                                source.Push(i);
                            source.Close(error_message: null);
                        }, cancel_token);
                        received_method_streamed1 = true;
                        return source;
                    },
                }
            );
        }
        Assert.IsTrue(received_method_streamed1, $"MethodStreamed1Deferred did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of MethodStreamed1Deferred", ex);

        Boolean received_method_streamed2 = false;
        {
            var len_existing = 10;
            var len = len_existing + 124;
            await TestMethod(
                send: () =>
                {
                    var received_x = -1;
                    var received = new List<Int32>();
                    var wh = new ManualResetEventSlim();
                    ExampleRpcApi.MethodStreamed2Deferred(async (x, stream) =>
                    {
                        received_x = x;
                        await foreach (var item in stream.ReadItemsAsync())
                            received.Add(item);
                        wh.Set();
                    }, read_cancel_token: default);
                    Assert.IsTrue(wh.Wait(TimeSpan.FromSeconds(5)), $"MethodStreamed2Deferred callback was not invoked");
                    Assert.AreEqual(len, received_x);
                    Assert.IsTrue(received.SequenceEqual(Enumerable.Range(0, len)), "MethodStreamed2Deferred received sequence does not match expected");
                },
                new()
                {
                    OnMethodStreamed2Deferred = (cancel_token) =>
                    {
                        var source = new RpcEnumerableSource<Int32>(f => f.CreatePermaHolder(), ping_stop_token: default);
                        var i = 0;
                        for (; i < len_existing; i++)
                            source.Push(i);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            for (; i < len; i++)
                                source.Push(i);
                            source.Close(error_message: null);
                        }, cancel_token);
                        received_method_streamed2 = true;
                        return (len, source);
                    },
                }
            );
        }
        Assert.IsTrue(received_method_streamed2, $"MethodStreamed2Deferred did not fire");
        foreach (var ex in errors)
            throw new Exception($"Error during testing of MethodStreamed2Deferred", ex);

        {
            var len_existing = 10;
            var len = len_existing + 124;
            await TestMethod(
                send: () =>
                {
                    var received = new List<Int32>();
                    var result = ExampleRpcApi.MethodStreamed3Blocking(len, async stream =>
                    {
                        await foreach (var item in stream.ReadItemsAsync())
                            received.Add(item);
                        return 789;
                    }, read_cancel_token: default);
                    Assert.AreEqual(789, result);
                    Assert.IsTrue(received.SequenceEqual(Enumerable.Range(0, len)), "MethodStreamed3Blocking received sequence does not match expected");
                },
                new()
                {
                    OnMethodStreamed3Blocking = (input, cancel_token) =>
                    {
                        Assert.AreEqual(len, input);
                        var source = new RpcEnumerableSource<Int32>(f => f.CreatePermaHolder(), ping_stop_token: default);
                        var i = 0;
                        for (; i < len_existing; i++)
                            source.Push(i);
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(100);
                            for (; i < len; i++)
                                source.Push(i);
                            source.Close(error_message: null);
                        }, cancel_token);
                        return source;
                    },
                }
            );
        }

        await Assert.ThrowsExceptionAsync<ExampleRpcApi.ExpectedException>(() => TestMethod(
            send: () =>
            {
                var ex = Assert.ThrowsException<RpcConnectionReturnedErrorException>(() =>
                {
                    ExampleRpcApi.Method3ExpectedException();
                });
                Assert.IsTrue(ex.Message.Contains(nameof(ExampleRpcApi.ExpectedException)), $"Error message does not contain '{nameof(ExampleRpcApi.ExpectedException)}': {ex.Message}");
            },
            new()
            {
                OnMethod3ExpectedException = (_) =>
                    throw new ExampleRpcApi.ExpectedException(),
            }
        ));

    }

}

internal static partial class ExampleRpcApi
{

    [RpcApi, RpcApiBlocking(TriesLimit = 3)]
    public static partial void Method1(Int32 x);

    [RpcApi, RpcApiBlocking(TriesLimit = 5)]
    public static partial Int32 Method2();

    [RpcApi, RpcApiDeferred(MinInitialConnectErrorCountBeforeReport = 1)]
    public static partial void MethodStreamed1Deferred(Int32 input, Func<RpcEnumerable<Int32>, Task> on_connected, CancellationToken read_cancel_token);

    [RpcApi, RpcApiDeferred(MinInitialConnectErrorCountBeforeReport = 1)]
    public static partial void MethodStreamed2Deferred(Func<Int32, RpcEnumerable<Int32>, Task> on_connected, CancellationToken read_cancel_token);

    [RpcApi, RpcApiBlocking(TriesLimit = 1)]
    public static partial Int32 MethodStreamed3Blocking(Int32 input, Func<RpcEnumerable<Int32>, Task<Int32>> on_connected, CancellationToken read_cancel_token);

    public sealed class ExpectedException : Exception;
    [RpcApi]
    public static partial void Method3ExpectedException();

}
