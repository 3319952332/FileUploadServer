namespace FileUploadServer.WsClient.Protocol;

/// <summary>
/// 二进制帧结构定义
///
/// 帧头格式 (24 bytes):
/// ┌──────────────┬──────────┬────────┬─────────────────────────┐
/// │ requestId     │ 16 B     │ 0      │ GUID，二进制表示        │
/// │ chunkIndex    │ 4 B      │ 16     │ 大端序 uint32，分块序号 │
/// │ totalChunks   │ 4 B      │ 20     │ 大端序 uint32，总分块数 │
/// └──────────────┴──────────┴────────┴─────────────────────────┘
///
/// 帧结构:
/// [requestId (16B)] [chunkIndex (4B)] [totalChunks (4B)] [文件数据 (变长)]
/// </summary>
public static class WsBinaryFrame
{
    /// <summary>
    /// 帧头大小：24 字节
    /// </summary>
    public const int HeaderSize = 24;

    /// <summary>
    /// 建议载荷大小：64KB
    /// </summary>
    public const int ChunkSize = 65536;

    /// <summary>
    /// 最大载荷大小：1MB
    /// </summary>
    public const int MaxPayloadSize = 1024 * 1024;

    /// <summary>
    /// 构建二进制帧
    /// </summary>
    /// <param name="requestId">请求唯一标识</param>
    /// <param name="chunkIndex">当前分块序号（从0开始）</param>
    /// <param name="totalChunks">总分块数（-1 表示未知）</param>
    /// <param name="data">文件数据</param>
    /// <param name="dataLength">实际数据长度</param>
    /// <returns>完整的二进制帧</returns>
    public static byte[] BuildFrame(Guid requestId, int chunkIndex, int totalChunks, byte[] data, int dataLength)
    {
        var frame = new byte[HeaderSize + dataLength];

        // requestId: 16 bytes (GUID binary)
        requestId.TryWriteBytes(frame.AsSpan(0, 16));

        // chunkIndex: 4 bytes (big-endian)
        frame[16] = (byte)((uint)chunkIndex >> 24);
        frame[17] = (byte)((uint)chunkIndex >> 16);
        frame[18] = (byte)((uint)chunkIndex >> 8);
        frame[19] = (byte)chunkIndex;

        // totalChunks: 4 bytes (big-endian)
        frame[20] = (byte)((uint)totalChunks >> 24);
        frame[21] = (byte)((uint)totalChunks >> 16);
        frame[22] = (byte)((uint)totalChunks >> 8);
        frame[23] = (byte)totalChunks;

        // payload data
        Array.Copy(data, 0, frame, HeaderSize, dataLength);

        return frame;
    }

    /// <summary>
    /// 解析二进制帧
    /// </summary>
    /// <param name="frame">完整的二进制帧数据</param>
    /// <returns>(requestId, chunkIndex, totalChunks, payload)</returns>
    public static (Guid requestId, int chunkIndex, int totalChunks, byte[] payload) ParseFrame(byte[] frame)
    {
        if (frame.Length < HeaderSize)
            throw new ArgumentException($"Frame too small: {frame.Length} bytes, minimum is {HeaderSize} bytes", nameof(frame));

        var requestIdBytes = new byte[16];
        Array.Copy(frame, 0, requestIdBytes, 0, 16);
        var requestId = new Guid(requestIdBytes);

        var chunkIndex = (frame[16] << 24) | (frame[17] << 16) | (frame[18] << 8) | frame[19];
        var totalChunks = (frame[20] << 24) | (frame[21] << 16) | (frame[22] << 8) | frame[23];

        var payloadLength = frame.Length - HeaderSize;
        var payload = new byte[payloadLength];
        Array.Copy(frame, HeaderSize, payload, 0, payloadLength);

        return (requestId, chunkIndex, totalChunks, payload);
    }
}
