using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileUploadServer.Core.Interfaces;
using FileUploadServer.WsClient.Protocol;

namespace FileUploadServer.WsClient;

/// <summary>
/// WebSocket 文件存储客户端
///
/// 通过 WebSocket 连接到文件服务器网关，负责实际的文件磁盘 I/O。
/// 支持心跳、断线重连（指数退避）、流式上传/下载。
/// </summary>
public class WsFileStorageClient : IFileStorageClient, IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _storagePath;
    private ClientWebSocket? _webSocket;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseMessage>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, DownloadContext> _downloads = new();
    private Timer? _heartbeatTimer;
    private CancellationTokenSource _disconnectCts = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private Task? _receiveLoopTask;
    private bool _disposed;

    // 重连策略
    private int _reconnectAttempt;
    private static readonly int[] ReconnectDelays = { 1000, 2000, 4000, 8000, 15000, 30000 };

    /// <summary>
    /// 是否已连接
    /// </summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>
    /// 断开连接事件
    /// </summary>
    public event EventHandler<DisconnectEventArgs>? OnDisconnected;

    /// <summary>
    /// 支持的路径前缀
    /// </summary>
    public string[] SupportedPaths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 创建 WsFileStorageClient 实例
    /// </summary>
    public WsFileStorageClient(string serverUrl, string clientId, string clientSecret, string storagePath = "")
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _clientId = clientId;
        _clientSecret = clientSecret;
        _storagePath = storagePath;
    }

    /// <summary>
    /// 将远程路径解析为安全的本地存储路径
    /// </summary>
    private string GetSafePath(string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains(".."))
            throw new InvalidOperationException($"Path traversal detected: {remotePath}");
        return Path.Combine(_storagePath, normalized);
    }

    /// <summary>
    /// 连接到服务端
    /// </summary>
    public async Task ConnectAsync(string serverUrl, string clientId, string clientSecret)
    {
        // 忽略参数，使用构造时传入的值
        await ConnectInternalAsync();
    }

    private async Task ConnectInternalAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (IsConnected)
                return;

            _disconnectCts = new CancellationTokenSource();

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var token = ComputeToken(_clientId, _clientSecret, timestamp);
            var prefixesStr = string.Join(",", SupportedPaths);
            var uri = new Uri($"{_serverUrl}/ws/connect?clientId={_clientId}&token={token}&timestamp={timestamp}&prefixes={Uri.EscapeDataString(prefixesStr)}");

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _webSocket.ConnectAsync(uri, connectCts.Token);

            _reconnectAttempt = 0;
            StartHeartbeat();
            _receiveLoopTask = ReceiveLoopAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// 断开连接
    /// </summary>
    public async Task DisconnectAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _disconnectCts.Cancel();

        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client shutting down",
                    CancellationToken.None);
            }
            catch
            {
                // 忽略关闭时的异常
            }
        }

        // 取消所有待处理的请求
        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetException(new WsClientException("Client disconnected"));
        }
        _pendingRequests.Clear();

        // 取消所有活跃下载/上传
        foreach (var kvp in _downloads)
        {
            if (kvp.Value.FileStream != null)
            {
                try { kvp.Value.FileStream.Close(); } catch { }
            }
            else
            {
                kvp.Value.PipeWriter.Complete(new WsClientException("Client disconnected"));
            }
        }
        _downloads.Clear();
    }

    /// <summary>
    /// 接收循环 - 持续接收并处理 WebSocket 消息
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 64]; // 64KB
        var messageBuffer = new MemoryStream();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !_disconnectCts.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    _disconnectCts.Token);

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Text:
                        messageBuffer.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            messageBuffer.SetLength(0);
                            await HandleTextMessage(json);
                        }
                        break;

                    case WebSocketMessageType.Binary:
                        await HandleBinaryMessage(new ReadOnlyMemory<byte>(buffer, 0, result.Count), result.EndOfMessage);
                        break;

                    case WebSocketMessageType.Close:
                        await HandleClose();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (WebSocketException)
        {
            // 连接断开
        }
        catch (ObjectDisposedException)
        {
            // WebSocket 已释放
        }
        finally
        {
            await OnConnectionLost();
        }
    }

    /// <summary>
    /// 处理文本消息
    /// </summary>
    private async Task HandleTextMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            var requestId = doc.RootElement.TryGetProperty("requestId", out var ridProp)
                ? ridProp.GetString() ?? string.Empty
                : string.Empty;

            switch (type)
            {
                case "upload_request":
                    await HandleUploadRequest(doc);
                    break;

                case "upload_complete":
                    // 服务端通知上传数据已全部发送 — 关闭本地文件流
                    if (!string.IsNullOrEmpty(requestId) && _downloads.TryRemove(requestId, out var upCtx))
                    {
                        try
                        {
                            if (upCtx.FileStream != null)
                            {
                                await upCtx.FileStream.FlushAsync();
                                upCtx.FileStream.Close();
                            }
                            else
                            {
                                upCtx.PipeWriter.Complete();
                            }
                            // 发送确认
                            await SendTextAsync(JsonSerializer.Serialize(new
                            {
                                type = "upload_complete",
                                requestId,
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                        }
                        catch (Exception ex)
                        {
                            await SendTextAsync(JsonSerializer.Serialize(new
                            {
                                type = "upload_error",
                                requestId,
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                code = 500,
                                message = ex.Message,
                            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                        }
                    }
                    break;

                case "download_request":
                    await HandleDownloadRequest(doc);
                    break;

                case "delete_request":
                    await HandleDeleteRequest(doc);
                    break;

                case "upload_ack":
                case "download_complete":
                case "delete_complete":
                    // 请求完成响应 - 匹配待处理请求
                    if (!string.IsNullOrEmpty(requestId) &&
                        _pendingRequests.TryRemove(requestId, out var tcs))
                    {
                        tcs.TrySetResult(new ResponseMessage(type ?? string.Empty, doc));
                    }
                    break;

                case "pong":
                    // 心跳响应，无需处理
                    break;

                case "error":
                    var errMsg = doc.RootElement.TryGetProperty("message", out var msgProp)
                        ? msgProp.GetString() ?? "Unknown error"
                        : "Unknown error";
                    if (!string.IsNullOrEmpty(requestId) &&
                        _pendingRequests.TryRemove(requestId, out var errTcs))
                    {
                        errTcs.TrySetException(new WsClientException(errMsg));
                    }
                    break;

                default:
                    // 未知消息类型，忽略
                    break;
            }
        }
        catch (JsonException)
        {
            // 无效 JSON，忽略
        }
    }

    /// <summary>
    /// 处理上传请求（服务端发来的上传指令）。
    /// 接收二进制文件数据块，写入本地磁盘。
    /// </summary>
    private async Task HandleUploadRequest(JsonDocument doc)
    {
        var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
        var path = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;

        try
        {
            // 解析文件路径并创建目录
            var safePath = GetSafePath(path);
            var dir = Path.GetDirectoryName(safePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 发送 upload_ack 确认接收
            var ackJson = JsonSerializer.Serialize(new
            {
                type = "upload_ack",
                requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                code = 200,
                message = "Ready to receive",
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await SendTextAsync(ackJson);

            // 打开目标文件写入流
            var fileStream = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            var ctx = new DownloadContext
            {
                FileStream = fileStream,
                FilePath = path,
            };
            _downloads[requestId] = ctx;
        }
        catch (Exception ex)
        {
            // 发送 upload_error
            var errorJson = JsonSerializer.Serialize(new
            {
                type = "upload_error",
                requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                code = 500,
                message = ex.Message,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await SendTextAsync(errorJson);
        }
    }

    /// <summary>
    /// 处理删除请求（服务端发来的删除指令）。
    /// 删除本地磁盘上的文件。
    /// </summary>
    private async Task HandleDeleteRequest(JsonDocument doc)
    {
        var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
        var path = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;

        try
        {
            var safePath = GetSafePath(path);
            if (File.Exists(safePath))
            {
                File.Delete(safePath);
            }

            // 发送 delete_complete
            var completeJson = JsonSerializer.Serialize(new
            {
                type = "delete_complete",
                requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await SendTextAsync(completeJson);
        }
        catch (Exception ex)
        {
            var errorJson = JsonSerializer.Serialize(new
            {
                type = "delete_error",
                requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                code = 500,
                message = ex.Message,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await SendTextAsync(errorJson);
        }
    }

    /// <summary>
    /// 处理下载请求（服务端请求下载文件）。
    /// 读取本地文件，通过二进制帧发送回去。
    /// </summary>
    private async Task HandleDownloadRequest(JsonDocument doc)
    {
        var requestId = doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
        var path = doc.RootElement.GetProperty("path").GetString() ?? string.Empty;

        try
        {
            var safePath = GetSafePath(path);
            if (!File.Exists(safePath))
            {
                var errJson = JsonSerializer.Serialize(new
                {
                    type = "download_error",
                    requestId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    code = 404,
                    message = $"File not found: {path}",
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                await SendTextAsync(errJson);
                return;
            }

            var fileBytes = await File.ReadAllBytesAsync(safePath);
            var requestGuid = Guid.Parse(requestId);
            const int chunkSize = 64 * 1024;
            var totalChunks = (int)Math.Ceiling((double)fileBytes.Length / chunkSize);

            // 分块发送文件数据
            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * chunkSize;
                var size = Math.Min(chunkSize, fileBytes.Length - offset);
                var chunk = new byte[size];
                Array.Copy(fileBytes, offset, chunk, 0, size);

                var frame = WsBinaryFrame.BuildFrame(requestGuid, i, totalChunks, chunk, chunk.Length);
                await _webSocket!.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, _disconnectCts.Token);
            }

            // 发送完成通知
            var completeJson = JsonSerializer.Serialize(new
            {
                type = "download_complete",
                requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await SendTextAsync(completeJson);
        }
        catch (Exception ex)
        {
            var errorJson = JsonSerializer.Serialize(new
            {
                type = "download_error",
                requestId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                code = 500,
                message = ex.Message,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await SendTextAsync(errorJson);
        }
    }

    /// <summary>
    /// 处理二进制消息（文件数据块）
    /// </summary>
    private async Task HandleBinaryMessage(ReadOnlyMemory<byte> data, bool endOfMessage)
    {
        if (data.Length < WsBinaryFrame.HeaderSize)
            return;

        try
        {
            var (requestId, chunkIndex, totalChunks, payload) = WsBinaryFrame.ParseFrame(data.ToArray());
            var requestIdStr = requestId.ToString();

            if (_downloads.TryGetValue(requestIdStr, out var ctx))
            {
                // 优先写入 FileStream（upload 场景），否则写入 PipeWriter（download 场景）
                if (ctx.FileStream != null)
                {
                    await ctx.FileStream.WriteAsync(payload);
                }
                else
                {
                    await ctx.PipeWriter.WriteAsync(new ReadOnlyMemory<byte>(payload));

                    if (endOfMessage && totalChunks > 0 && chunkIndex == totalChunks - 1)
                    {
                        ctx.PipeWriter.Complete();
                        _downloads.TryRemove(requestIdStr, out _);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 解析失败，忽略该帧
        }
    }

    /// <summary>
    /// 处理连接关闭
    /// </summary>
    private async Task HandleClose()
    {
        if (_webSocket != null && _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 连接丢失处理
    /// </summary>
    private async Task OnConnectionLost()
    {
        var willReconnect = !_disconnectCts.IsCancellationRequested;

        OnDisconnected?.Invoke(this, new DisconnectEventArgs
        {
            Reason = "Connection lost",
            WillReconnect = willReconnect,
        });

        if (willReconnect)
        {
            await ReconnectAsync();
        }
    }

    /// <summary>
    /// 计算认证 token
    /// 服务端存储的是 SHA256(clientSecret)，并用它作为密钥材料计算 token。
    /// 因此客户端需要先对 clientSecret 做一次 SHA256，再参与 token 计算。
    /// token = SHA256(clientId + ":" + SHA256(clientSecret) + ":" + timestamp)
    /// </summary>
    private static string ComputeToken(string clientId, string clientSecret, long timestamp)
    {
        // 先对 secret 做一次哈希，与服务端存储的 ClientSecretHash 一致
        var secretHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret));
        var secretHash = Convert.ToHexString(secretHashBytes).ToLowerInvariant();
        var input = $"{clientId}:{secretHash}:{timestamp}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 发送文本消息
    /// </summary>
    private async Task SendTextAsync(string text)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new WsClientException("WebSocket is not connected");

        var bytes = Encoding.UTF8.GetBytes(text);
        await _webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            _disconnectCts.Token);
    }

    /// <summary>
    /// 发送二进制消息
    /// </summary>
    private async Task SendBinaryAsync(byte[] data)
    {
        if (_webSocket?.State != WebSocketState.Open)
            throw new WsClientException("WebSocket is not connected");

        await _webSocket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Binary,
            true,
            _disconnectCts.Token);
    }

    /// <summary>
    /// 启动心跳定时器
    /// </summary>
    private void StartHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(
            async _ => await SendHeartbeat(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 发送心跳
    /// </summary>
    private async Task SendHeartbeat()
    {
        try
        {
            var pingJson = JsonSerializer.Serialize(new
            {
                type = "ping",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await SendTextAsync(pingJson);
        }
        catch
        {
            // 心跳失败，重连逻辑在 ReceiveLoop 中处理
        }
    }

    /// <summary>
    /// 指数退避重连
    /// </summary>
    private async Task ReconnectAsync()
    {
        var delay = ReconnectDelays[Math.Min(_reconnectAttempt, ReconnectDelays.Length - 1)];
        _reconnectAttempt++;

        await Task.Delay(delay, _disconnectCts.Token);

        try
        {
            await ConnectInternalAsync();
        }
        catch
        {
            // 重连失败，继续尝试
            if (!_disconnectCts.IsCancellationRequested)
            {
                _ = ReconnectAsync();
            }
        }
    }

    /// <summary>
    /// 获取存储容量（当前返回 -1 表示无限制）
    /// </summary>
    private static long GetStorageCapacity() => -1;

    /// <summary>
    /// 读取文件（流式下载）
    /// </summary>
    public async Task<Stream> ReadFileAsync(string path)
    {
        var requestId = Guid.NewGuid();
        var requestIdStr = requestId.ToString();
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 65536 * 4)); // 256KB buffer

        var ctx = new DownloadContext
        {
            PipeWriter = pipe.Writer,
            TotalChunks = -1,
            FilePath = path,
        };
        _downloads[requestIdStr] = ctx;

        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = tcs;

        // 发送下载请求
        var requestJson = JsonSerializer.Serialize(new
        {
            type = "download_request",
            requestId = requestIdStr,
            path,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await SendTextAsync(requestJson);

        // 等待 download_complete 确认（30s 超时）
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        return pipe.Reader.AsStream();
    }

    /// <summary>
    /// 写入文件（分块上传）
    /// </summary>
    public async Task WriteFileAsync(string path, Stream data)
    {
        var requestId = Guid.NewGuid();
        var requestIdStr = requestId.ToString();

        // 1. 发送上传请求
        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = tcs;

        var requestJson = JsonSerializer.Serialize(new
        {
            type = "upload_request",
            requestId = requestIdStr,
            path,
            fileSize = data.Length,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await SendTextAsync(requestJson);

        // 2. 等待 upload_ack (5s 超时)
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 3. 分块发送文件数据
        var buffer = new byte[WsBinaryFrame.ChunkSize];
        int chunkIndex = 0;
        int bytesRead;
        long totalBytesRead = 0;
        long totalLength = data.Length;

        while ((bytesRead = await data.ReadAsync(buffer, _disconnectCts.Token)) > 0)
        {
            totalBytesRead += bytesRead;
            var totalChunks = totalLength > 0
                ? (int)((totalLength + WsBinaryFrame.ChunkSize - 1) / WsBinaryFrame.ChunkSize)
                : -1;

            var frame = WsBinaryFrame.BuildFrame(requestId, chunkIndex, totalChunks, buffer, bytesRead);
            await SendBinaryAsync(frame);
            chunkIndex++;
        }

        // 4. 等待 upload_complete (30s 超时)
        var completeTcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = completeTcs;
        await completeTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 删除文件
    /// </summary>
    public async Task DeleteFileAsync(string path)
    {
        var requestId = Guid.NewGuid();
        var requestIdStr = requestId.ToString();

        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = tcs;

        var requestJson = JsonSerializer.Serialize(new
        {
            type = "delete_request",
            requestId = requestIdStr,
            path,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await SendTextAsync(requestJson);

        // 等待 delete_complete (10s 超时)
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    public async Task<bool> FileExistsAsync(string path)
    {
        var requestId = Guid.NewGuid();
        var requestIdStr = requestId.ToString();

        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = tcs;

        var requestJson = JsonSerializer.Serialize(new
        {
            type = "file_exists_request",
            requestId = requestIdStr,
            path,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await SendTextAsync(requestJson);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return result.Doc.RootElement.TryGetProperty("exists", out var existsProp) && existsProp.GetBoolean();
    }

    /// <summary>
    /// 获取文件大小
    /// </summary>
    public async Task<long> GetFileSizeAsync(string path)
    {
        var requestId = Guid.NewGuid();
        var requestIdStr = requestId.ToString();

        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = tcs;

        var requestJson = JsonSerializer.Serialize(new
        {
            type = "file_size_request",
            requestId = requestIdStr,
            path,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await SendTextAsync(requestJson);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return result.Doc.RootElement.GetProperty("fileSize").GetInt64();
    }

    /// <summary>
    /// 获取文件哈希
    /// </summary>
    public async Task<string> GetFileHashAsync(string path)
    {
        var requestId = Guid.NewGuid();
        var requestIdStr = requestId.ToString();

        var tcs = new TaskCompletionSource<ResponseMessage>();
        _pendingRequests[requestIdStr] = tcs;

        var requestJson = JsonSerializer.Serialize(new
        {
            type = "file_hash_request",
            requestId = requestIdStr,
            path,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await SendTextAsync(requestJson);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return result.Doc.RootElement.GetProperty("fileHash").GetString() ?? string.Empty;
    }

    /// <summary>
    /// Dispose 异步释放资源
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        _heartbeatTimer?.Dispose();
        _disconnectCts.Dispose();
        _connectLock.Dispose();
        _webSocket?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 响应消息包装
    /// </summary>
    internal class ResponseMessage
    {
        public string Type { get; }
        public JsonDocument Doc { get; }

        public ResponseMessage(string type, JsonDocument doc)
        {
            Type = type;
            Doc = doc;
        }
    }

    /// <summary>
    /// 下载上下文
    /// </summary>
    internal class DownloadContext
    {
        public PipeWriter PipeWriter { get; set; } = null!;
        public FileStream? FileStream { get; set; }
        public int TotalChunks { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public TaskCompletionSource<bool> CompletionSource { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// WS 客户端异常
/// </summary>
public class WsClientException : Exception
{
    public WsClientException(string message) : base(message) { }
    public WsClientException(string message, Exception inner) : base(message, inner) { }
}
