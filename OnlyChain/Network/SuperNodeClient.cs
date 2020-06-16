﻿#nullable enable

using OnlyChain.Core;
using OnlyChain.Secp256k1;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnlyChain.Network {
    /// <summary>
    /// 超级节点出块专用
    /// </summary>
    public sealed class SuperNodeClient : IDisposable {
        private readonly ThreadLocal<Random> random = new ThreadLocal<Random>(() => new Random(), trackAllValues: false);
        private readonly IClient client;
        private readonly BlockChainSystem system;
        private readonly Socket serverSocket; // 

        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        public IPEndPoint BindIPEndPoint { get; }

        public event EventHandler<SuperNodeEventArgs>? ClientConnected;
        public event EventHandler<SuperNodeEventArgs>? DataArrived;
        public event EventHandler<SuperNodeEventArgs>? Closed;

        private SuperNodeClient(IClient myClient, IPEndPoint bindEP) {
            client = myClient;
            system = myClient.System;
            BindIPEndPoint = bindEP;

            serverSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            serverSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            serverSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); // 启用tcp心跳
            serverSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 2); // 重试次数
            serverSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60); // 冷却间隔秒数
            serverSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1); // 重试间隔秒数
            serverSocket.Bind(bindEP);
            serverSocket.Listen(2);

            StartAccept();

            client.System.CampaignNodesChanged += CampaignNodesChanged;
            client.ReceiveBroadcast += Client_ReceiveBroadcast;
        }

        private void CampaignNodesChanged(object? sender, CampaignNodesChangedEventArgs e) {

        }

        private void Client_ReceiveBroadcast(object? sender, BroadcastEventArgs e) {
            // 只处理超级节点上线广播
            // 1 bytes: 0xff
            // 1 bytes: ip版本
            // 16,4 bytes: ip
            // 2 bytes: port
            // 4 bytes: 区块链时间戳（超过1小时丢弃）
            // 32 bytes: 随机数
            // 64 bytes: 超级节点公钥
            // 64 bytes: 签名
            if (e.Message.Length > 0 && e.Message[0] is 0xff) {
                ReadOnlyMemory<byte> data = e.Message.AsMemory(1);
                int ipBytes;
                IPAddress ipAddress;
                if (data.Span[0] is 4) {
                    ipAddress = new IPAddress(data.Span.Slice(1, 4));
                    ipBytes = 4;
                } else if (data.Span[0] is 6) {
                    ipAddress = new IPAddress(data.Span.Slice(1, 16));
                    ipBytes = 16;
                } else goto CancelForward;

                int port = BinaryPrimitives.ReadUInt16BigEndian(data.Span.Slice(1 + ipBytes));
                var endPoint = new IPEndPoint(ipAddress, port);

                var time = BlockChainTimestamp.ToDateTime(BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(3 + ipBytes)));
                if (DateTime.Now - time >= TimeSpan.FromHours(1) || time - DateTime.Now >= TimeSpan.FromMinutes(5)) goto CancelForward; // 过期，丢弃并阻断广播

                var (publicKey, _) = Deserializer.PublicKeyStruct(data.Span.Slice(39 + ipBytes));
                var address = publicKey.ToAddress();
                if (!client.System.ImmutableCampaignNodes.TryGetValue(address, out SuperNode? oldSuperNode)) goto CancelForward; // 非竞选节点，丢弃并阻断广播

                var (sign, _) = Deserializer.Signature(data.Span.Slice(103 + ipBytes));
                if (!Ecdsa.Verify(publicKey, data.Span.Slice(0, 103 + ipBytes).MessageHash(), sign)) goto CancelForward; // 错误的签名，丢弃并阻断广播

                SuperNode superNode;
                if (endPoint.Equals(oldSuperNode?.IPEndPoint)) { // IP 端口与本地保存的一致
                    superNode = oldSuperNode;
                } else {
                    superNode = new SuperNode(publicKey, endPoint);
                    client.System.ImmutableCampaignNodes[address] = superNode;
                }

                if (client.System.IsProducer(address) && !superNode.Connected) {
                    e.Task = superNode.ConnectAsync().ContinueWith(task => {
                        task.Wait();

                    });
                }
            }
            return;

        CancelForward:
            e.CancelForward();
        }

        private async void StartAccept() {
            while (!cancellationTokenSource.IsCancellationRequested) {
                try {
                    Socket client = await serverSocket.AcceptAsync();
                    HandleClient(client);
                } catch { break; }
            }
        }

        private async void HandleClient(Socket clientSocket) {
            try {
                clientSocket.NoDelay = true;

                using var stream = new NetworkStream(clientSocket, ownsSocket: true) {
                    ReadTimeout = 3000,
                    WriteTimeout = 3000,
                };

                var messageBuffer = new byte[64];
                random.Value!.NextBytes(messageBuffer.AsSpan(0, 32));
                await stream.WriteAsync(messageBuffer.AsMemory(0, 32), cancellationTokenSource.Token);

                // 32字节另一半消息
                // 64字节公钥
                // 64字节签名
                var buffer = new byte[160];
                await stream.FillAsync(buffer, cancellationTokenSource.Token);
                buffer.AsSpan(0, 32).CopyTo(messageBuffer.AsSpan(32));
                byte[] messageHash = Sha256.ComputeHashToArray(messageBuffer);
                if (messageHash[0] != 0) return; // 工作量证明，hash前8位必须是0
                var (pubkey, sign) = ReadPublicKeySignature(buffer.AsSpan(32));
                var superAddress = pubkey.ToAddress();
                if (!system.IsProducer(superAddress)) return;
                if (!Secp256k1.Secp256k1.Verify(pubkey, messageHash, sign)) return;

                // 认证通过，开始处理请求
                stream.ReadTimeout = -1;
                var clientNode = new SuperNode(pubkey, (IPEndPoint)clientSocket.RemoteEndPoint, isReadOnly: true) {
                    ClientSocket = clientSocket
                };
                ClientConnected?.Invoke(this, new SuperNodeEventArgs(clientNode, Array.Empty<byte>()));

                try {
                    var arrayPool = ArrayPool<byte>.Create(65536, 1);
                    while (cancellationTokenSource.IsCancellationRequested is false && clientSocket.Connected) {
                        var packetLength = await stream.ReadStructAsync<int>(cancellationTokenSource.Token);
                        if (BitConverter.IsLittleEndian is false)
                            packetLength = BinaryPrimitives.ReverseEndianness(packetLength);
                        if (packetLength is 0 || packetLength > 10485760) return;

                        byte[] packetBuffer = arrayPool.Rent(packetLength);
                        await stream.FillAsync(packetBuffer.AsMemory(0, packetLength), cancellationTokenSource.Token);

                        // TODO: 处理请求
                        DataArrived?.Invoke(this, new SuperNodeEventArgs(clientNode, packetBuffer.AsMemory(0, packetLength)));

                        arrayPool.Return(packetBuffer);
                    }
                } catch {
                }
                Closed?.Invoke(this, new SuperNodeEventArgs(clientNode, Array.Empty<byte>()));
            } catch {

            }

            static (PublicKey, Signature) ReadPublicKeySignature(ReadOnlySpan<byte> buffer) {
                var deserializer = new Deserializer(buffer);
                PublicKey publicKey = deserializer.Read(Deserializer.PublicKeyStruct);
                Signature signature = deserializer.Read(Deserializer.Signature);
                return (publicKey, signature);
            }
        }

        public static async Task<SuperNodeClient> Create(IClient myClient, IPEndPoint bindEP, CancellationToken cancellationToken = default) {
            async ValueTask FindNodeTask(Address address) {
                if (myClient.System.ImmutableCampaignNodes.TryGetValue(address, out SuperNode? superNode) is false || superNode is { }) return;
                PublicKey? pubkey = myClient.System.GetPublicKey(address);
                if (pubkey is null) return;

                Node? node = await myClient.Lookup(address, cancellationToken: cancellationToken);
                if (node is { }) {
                    try {
                        superNode = new SuperNode(pubkey, node.IPEndPoint);
                        Task task = superNode.ConnectAsync();
                        myClient.System.ImmutableCampaignNodes[address] = superNode;
                        await task;
                    } catch {

                    }
                }
            }

            var client = new SuperNodeClient(myClient, bindEP);
            Address[] campaignNodes = myClient.System.ImmutableCampaignNodes.Keys.Take(Constants.MinProducerCount).ToArray();
            await campaignNodes
                .Select(address => FindNodeTask(address).AsTask())
                .WhenAll();
            return client;
        }

        public void Dispose() {

        }
    }
}
