using System.Buffers;
using System.Security.Cryptography;
using FileUploadServer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace FileUploadServer.Infrastructure.Encryption;

/// <summary>
/// 加密文件格式常量定义
/// </summary>
public static class EncryptedFileConstants
{
    /// <summary>
    /// 文件魔数 "FUEC" = FileUpload Encrypted
    /// </summary>
    public const string Magic = "FUEC";

    /// <summary>
    /// 当前格式版本
    /// </summary>
    public const ushort FormatVersion = 0x0001;

    /// <summary>
    /// 文件头大小（48 字节）
    /// </summary>
    public const int HeaderSize = 48;

    /// <summary>
    /// Nonce 大小（12 字节，GCM 标准）
    /// </summary>
    public const int NonceSize = 12;

    /// <summary>
    /// GCM 认证标签大小（16 字节）
    /// </summary>
    public const int AuthTagSize = 16;

    /// <summary>
    /// 默认块大小（1 MB）
    /// </summary>
    public const int DefaultBlockSize = 1_048_576;

    /// <summary>
    /// 每块在加密流中的固定开销：Nonce + AuthTag
    /// </summary>
    public const int ChunkOverhead = NonceSize + AuthTagSize; // 28 字节
}

/// <summary>
/// 分块 AES-256-GCM 加密流
/// 写入时透明加密，将明文分块加密后写入内部流
/// 文件格式：
///   [Header 48B] [Chunk0: Nonce12B + Ciphertext + AuthTag16B] [Chunk1: ...] ...
/// </summary>
public class AesGcmEncryptStream : Stream
{
    private readonly Stream _innerStream;
    private readonly byte[] _masterKey;
    private readonly ushort _keyVersion;
    private readonly int _blockSize;
    private readonly byte[] _pendingBuffer;
    private int _pendingCount;
    private bool _headerWritten;
    private bool _disposed;
    private long _plaintextLength;
    private readonly ILogger? _logger;

    /// <summary>
    /// 初始化加密流
    /// </summary>
    /// <param name="innerStream">写入加密数据的目标流</param>
    /// <param name="masterKey">32 字节的 AES-256 Master Key</param>
    /// <param name="keyVersion">密钥版本号</param>
    /// <param name="blockSize">每块明文大小（字节），默认 1MB</param>
    /// <param name="logger">日志记录器（可选）</param>
    /// <exception cref="ArgumentNullException">innerStream 或 masterKey 为 null</exception>
    /// <exception cref="ArgumentException">masterKey 长度不是 32 字节</exception>
    public AesGcmEncryptStream(
        Stream innerStream,
        byte[] masterKey,
        ushort keyVersion = 1,
        int blockSize = EncryptedFileConstants.DefaultBlockSize,
        ILogger? logger = null)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _masterKey = masterKey ?? throw new ArgumentNullException(nameof(masterKey));
        if (masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes (256 bits).", nameof(masterKey));
        if (blockSize <= 0 || blockSize > int.MaxValue - EncryptedFileConstants.ChunkOverhead)
            throw new ArgumentException($"Block size must be positive and less than {int.MaxValue - EncryptedFileConstants.ChunkOverhead}.", nameof(blockSize));

        _keyVersion = keyVersion;
        _blockSize = blockSize;
        _pendingBuffer = new byte[_blockSize];
        _logger = logger;

        // 预占文件头空间（48 字节），写入完成后回写真实文件头
        var placeholder = new byte[EncryptedFileConstants.HeaderSize];
        _innerStream.Write(placeholder, 0, placeholder.Length);

        _logger?.LogDebug(
            "AesGcmEncryptStream initialized: BlockSize={BlockSize}, KeyVersion={KeyVersion}",
            _blockSize, _keyVersion);
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("Offset and count exceed buffer length.");

        int remaining = count;
        int currentOffset = offset;

        while (remaining > 0)
        {
            // 填充待处理缓冲区
            int space = _blockSize - _pendingCount;
            int toCopy = Math.Min(remaining, space);

            Buffer.BlockCopy(buffer, currentOffset, _pendingBuffer, _pendingCount, toCopy);
            _pendingCount += toCopy;
            currentOffset += toCopy;
            remaining -= toCopy;
            _plaintextLength += toCopy;

            // 待处理缓冲区满了，加密一个块
            if (_pendingCount >= _blockSize)
            {
                EncryptAndWriteBlock(final: false);
            }
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        if (_disposed) return;

        // 加密剩余的未对齐数据（最后一块）
        if (_pendingCount > 0)
        {
            EncryptAndWriteBlock(final: true);
        }

        // 如果没有写入任何数据，仍然写一个空块以确保文件格式合法
        if (!_headerWritten && _plaintextLength == 0)
        {
            EncryptAndWriteBlock(final: true);
        }

        // 回写文件头到流的起始位置
        WriteHeader();

        _innerStream.Flush();
        _logger?.LogDebug("AesGcmEncryptStream flushed. Total plaintext length: {Length}", _plaintextLength);
    }

    /// <summary>
    /// 加密当前待处理缓冲区并写入内部流
    /// </summary>
    /// <param name="final">是否为最后一个数据块</param>
    private void EncryptAndWriteBlock(bool final)
    {
        int plaintextLength = _pendingCount;
        if (plaintextLength == 0) return;

        byte[] nonce = new byte[EncryptedFileConstants.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertext = new byte[plaintextLength];
        byte[] tag = new byte[EncryptedFileConstants.AuthTagSize];

        using var aesGcm = new AesGcm(_masterKey, EncryptedFileConstants.AuthTagSize);
        aesGcm.Encrypt(
            nonce,
            _pendingBuffer.AsSpan(0, plaintextLength),
            ciphertext,
            tag);

        // 写入块：[Nonce 12B][Ciphertext][AuthTag 16B]
        _innerStream.Write(nonce, 0, nonce.Length);
        _innerStream.Write(ciphertext, 0, ciphertext.Length);
        _innerStream.Write(tag, 0, tag.Length);

        _pendingCount = 0;

        _logger?.LogTrace(
            "Encrypted block: PlaintextLength={PlaintextLength}, Nonce={Nonce}",
            plaintextLength, Convert.ToHexString(nonce));
    }

    /// <summary>
    /// 将文件头写入内部流的起始位置
    /// </summary>
    private void WriteHeader()
    {
        if (_headerWritten) return;
        _headerWritten = true;

        byte[] header = new byte[EncryptedFileConstants.HeaderSize];

        // Magic "FUEC" (4 字节)
        header[0] = 0x46; // 'F'
        header[1] = 0x55; // 'U'
        header[2] = 0x45; // 'E'
        header[3] = 0x43; // 'C'

        // Version 0x0001 (2 字节，大端序)
        header[4] = (byte)((EncryptedFileConstants.FormatVersion >> 8) & 0xFF);
        header[5] = (byte)(EncryptedFileConstants.FormatVersion & 0xFF);

        // KeyVersion (2 字节，大端序)
        header[6] = (byte)((_keyVersion >> 8) & 0xFF);
        header[7] = (byte)(_keyVersion & 0xFF);

        // BlockSize (4 字节，大端序)
        header[8] = (byte)((_blockSize >> 24) & 0xFF);
        header[9] = (byte)((_blockSize >> 16) & 0xFF);
        header[10] = (byte)((_blockSize >> 8) & 0xFF);
        header[11] = (byte)(_blockSize & 0xFF);

        // Reserved (36 字节) 保持为 0

        long currentPosition = _innerStream.Position;
        _innerStream.Position = 0;
        _innerStream.Write(header, 0, header.Length);
        _innerStream.Position = currentPosition;

        _logger?.LogDebug(
            "Encrypted file header written: Version={Version}, KeyVer={KeyVer}, BlockSize={BlockSize}",
            EncryptedFileConstants.FormatVersion, _keyVersion, _blockSize);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            try
            {
                Flush();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error flushing during AesGcmEncryptStream dispose.");
            }
            Array.Clear(_pendingBuffer, 0, _pendingBuffer.Length);
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("AesGcmEncryptStream does not support reading.");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("AesGcmEncryptStream does not support seeking.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("AesGcmEncryptStream does not support setting length.");
}

/// <summary>
/// 分块 AES-256-GCM 解密流
/// 读取时透明解密，从内部流读取加密数据并解密返回明文
/// 支持向前 Seek（按块索引定位）
/// </summary>
public class AesGcmDecryptStream : Stream
{
    private readonly Stream _innerStream;
    private readonly IKeyProvider _keyProvider;
    private byte[]? _masterKey;
    private ushort _keyVersion;
    private int _blockSize;
    private bool _headerParsed;
    private long _plaintextPosition;
    private byte[]? _currentDecryptedChunk;
    private int _chunkOffset;
    private int _currentChunkDataLength;
    private int _currentChunkIndex;
    private long _knownPlaintextLength;
    private readonly long _innerStreamAvailableLength;
    private readonly ILogger? _logger;

    /// <summary>
    /// 初始化解密流
    /// </summary>
    /// <param name="innerStream">读取加密数据的源流</param>
    /// <param name="keyProvider">密钥提供者，用于获取指定版本的 Master Key</param>
    /// <param name="logger">日志记录器（可选）</param>
    /// <exception cref="ArgumentNullException">innerStream 或 keyProvider 为 null</exception>
    public AesGcmDecryptStream(
        Stream innerStream,
        IKeyProvider keyProvider,
        ILogger? logger = null)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _logger = logger;

        if (innerStream.CanSeek)
        {
            _innerStreamAvailableLength = innerStream.Length;
        }
        else
        {
            _innerStreamAvailableLength = -1;
        }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => _innerStream.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            EnsureHeaderParsed();
            if (_knownPlaintextLength > 0) return _knownPlaintextLength;

            // 通过加密流长度估算明文长度
            if (_innerStreamAvailableLength > 0)
            {
                long encryptedDataLength = _innerStreamAvailableLength - EncryptedFileConstants.HeaderSize;
                if (encryptedDataLength <= 0) return 0;

                long chunkTotalSize = _blockSize + (long)EncryptedFileConstants.ChunkOverhead;
                long fullChunks = encryptedDataLength / chunkTotalSize;
                long lastChunkExtra = encryptedDataLength % chunkTotalSize;

                if (lastChunkExtra > 0)
                {
                    // 最后一块可能不满
                    long lastChunkCipherLen = lastChunkExtra - EncryptedFileConstants.ChunkOverhead;
                    if (lastChunkCipherLen > 0)
                        return fullChunks * _blockSize + lastChunkCipherLen;
                }

                return fullChunks * _blockSize;
            }

            throw new NotSupportedException("Cannot determine length for non-seekable stream.");
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get => _plaintextPosition;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("Offset and count exceed buffer length.");

        EnsureHeaderParsed();

        int totalRead = 0;
        int currentOffset = offset;
        int remaining = count;

        while (remaining > 0)
        {
            // 如果当前块已读完，加载下一个块
            if (_currentDecryptedChunk == null || _chunkOffset >= _currentChunkDataLength)
            {
                if (!LoadNextChunk())
                    break; // 没有更多数据
            }

            // 从当前解密块复制数据到输出缓冲区
            int available = _currentChunkDataLength - _chunkOffset;
            int toCopy = Math.Min(remaining, available);

            Buffer.BlockCopy(_currentDecryptedChunk!, _chunkOffset, buffer, currentOffset, toCopy);
            _chunkOffset += toCopy;
            currentOffset += toCopy;
            remaining -= toCopy;
            totalRead += toCopy;
            _plaintextPosition += toCopy;
        }

        return totalRead;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureHeaderParsed();
        if (!CanSeek)
            throw new NotSupportedException("Underlying stream does not support seeking.");

        long newPosition;
        switch (origin)
        {
            case SeekOrigin.Begin:
                newPosition = offset;
                break;
            case SeekOrigin.Current:
                newPosition = _plaintextPosition + offset;
                break;
            case SeekOrigin.End:
                newPosition = Length + offset;
                break;
            default:
                throw new ArgumentException("Invalid seek origin.", nameof(origin));
        }

        if (newPosition < 0)
            throw new IOException("Attempt to seek before the beginning of the stream.");

        if (newPosition == _plaintextPosition)
            return _plaintextPosition;

        // 计算目标块索引
        int targetChunk = (int)(newPosition / _blockSize);
        int targetOffsetInChunk = (int)(newPosition % _blockSize);

        if (targetChunk == _currentChunkIndex && _currentDecryptedChunk != null)
        {
            // 在同一块内寻址
            _chunkOffset = targetOffsetInChunk;
            _plaintextPosition = newPosition;
            return _plaintextPosition;
        }

        // 需要跳转到不同的块
        // 计算加密流中的块起始位置
        long encryptedChunkStart = EncryptedFileConstants.HeaderSize +
                                   (long)targetChunk * (_blockSize + EncryptedFileConstants.ChunkOverhead);

        if (encryptedChunkStart >= _innerStreamAvailableLength)
        {
            // 如果超出文件末尾，定位到文件末尾
            _plaintextPosition = Length;
            _currentDecryptedChunk = null;
            _currentChunkIndex = -1;
            return _plaintextPosition;
        }

        _innerStream.Seek(encryptedChunkStart, SeekOrigin.Begin);
        _currentChunkIndex = targetChunk - 1; // LoadNextChunk 会 +1
        _currentDecryptedChunk = null;
        _chunkOffset = 0;

        // 加载目标块
        if (!LoadNextChunk())
        {
            // 无法加载（超出文件范围）
            _plaintextPosition = Length;
            return _plaintextPosition;
        }

        _chunkOffset = targetOffsetInChunk;
        _plaintextPosition = newPosition;

        // 如果目标偏移超过当前块的实际数据长度，定位到块末尾
        if (_chunkOffset > _currentChunkDataLength)
        {
            _chunkOffset = _currentChunkDataLength;
            _plaintextPosition = (long)_currentChunkIndex * _blockSize + _currentChunkDataLength;
        }

        return _plaintextPosition;
    }

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException("AesGcmDecryptStream does not support flushing.");

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("AesGcmDecryptStream does not support setting length.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("AesGcmDecryptStream does not support writing.");

    /// <summary>
    /// 解析文件头（首次读取时调用）
    /// </summary>
    /// <exception cref="CryptographicException">文件头无效或密钥版本不受支持</exception>
    private void ParseHeader()
    {
        byte[] header = new byte[EncryptedFileConstants.HeaderSize];
        int read = _innerStream.Read(header, 0, header.Length);
        if (read < EncryptedFileConstants.HeaderSize)
        {
            throw new CryptographicException(
                $"Invalid encrypted file: header is too short ({read} bytes, expected {EncryptedFileConstants.HeaderSize}).");
        }

        // 验证 Magic
        if (header[0] != 0x46 || header[1] != 0x55 || header[2] != 0x45 || header[3] != 0x43)
        {
            throw new CryptographicException(
                "Invalid encrypted file: bad magic number. Expected 'FUEC'.");
        }

        ushort fileVersion = (ushort)((header[4] << 8) | header[5]);
        if (fileVersion != EncryptedFileConstants.FormatVersion)
        {
            throw new CryptographicException(
                $"Unsupported encrypted file format version: {fileVersion}. Expected: {EncryptedFileConstants.FormatVersion}.");
        }

        _keyVersion = (ushort)((header[6] << 8) | header[7]);
        _blockSize = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];

        if (_blockSize <= 0 || _blockSize > 100 * 1024 * 1024)
        {
            throw new CryptographicException(
                $"Invalid block size in header: {_blockSize}.");
        }

        // 获取对应版本的 Master Key
        if (!_keyProvider.SupportsKeyVersion(_keyVersion))
        {
            throw new CryptographicException(
                $"Key version {_keyVersion} is not supported by the current key provider.");
        }

        _masterKey = _keyProvider.GetMasterKey(_keyVersion);

        _logger?.LogDebug(
            "Encrypted file header parsed: Version={Version}, KeyVer={KeyVer}, BlockSize={BlockSize}",
            fileVersion, _keyVersion, _blockSize);
    }

    /// <summary>
    /// 确保文件头已解析
    /// </summary>
    private void EnsureHeaderParsed()
    {
        if (!_headerParsed)
        {
            ParseHeader();
            _headerParsed = true;
        }
    }

    /// <summary>
    /// 从内部流加载并解密下一个数据块
    /// </summary>
    /// <returns>如果成功加载并解密了一个数据块则返回 true；没有更多数据时返回 false</returns>
    private bool LoadNextChunk()
    {
        _currentChunkIndex++;

        // 读取 Nonce
        byte[] nonce = new byte[EncryptedFileConstants.NonceSize];
        int nonceRead = _innerStream.Read(nonce, 0, nonce.Length);
        if (nonceRead < EncryptedFileConstants.NonceSize)
        {
            // 没有更多数据
            _currentDecryptedChunk = null;
            return false;
        }

        // 读取 Ciphertext（读取到流末尾或块大小）
        // GCM 模式下密文长度等于明文长度。最后一个块可能不满 _blockSize。
        // 先把 nonce 之后所有可用数据读出来，再分离密文和认证标签。
        byte[] rawData = new byte[_blockSize + EncryptedFileConstants.AuthTagSize];
        int rawDataRead = 0;
        int rawCapacity = rawData.Length;

        while (rawDataRead < rawCapacity)
        {
            int chunkRead = _innerStream.Read(rawData, rawDataRead, rawCapacity - rawDataRead);
            if (chunkRead == 0) break;
            rawDataRead += chunkRead;
        }

        if (rawDataRead < EncryptedFileConstants.AuthTagSize)
        {
            if (rawDataRead > 0)
                throw new CryptographicException(
                    $"Truncated encrypted file: expected at least {EncryptedFileConstants.AuthTagSize} bytes for auth tag, but only {rawDataRead} byte(s) at chunk {_currentChunkIndex}.");
            _currentDecryptedChunk = null;
            return false;
        }

        // 最后 16 字节是认证标签，其余是密文
        int ciphertextRead = rawDataRead - EncryptedFileConstants.AuthTagSize;
        byte[] ciphertext = rawData.AsSpan(0, ciphertextRead).ToArray();
        byte[] tag = rawData.AsSpan(ciphertextRead, EncryptedFileConstants.AuthTagSize).ToArray();

        // 解密
        byte[] plaintext = new byte[ciphertextRead];
        try
        {
            if (_masterKey is null)
                throw new CryptographicException("Master key not available for decryption.");
            using var aesGcm = new AesGcm(_masterKey, EncryptedFileConstants.AuthTagSize);
            aesGcm.Decrypt(nonce, ciphertext.AsSpan(0, ciphertextRead), tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger?.LogError(ex,
                "Authentication tag validation failed at chunk {ChunkIndex}. The file may be corrupted or tampered with.",
                _currentChunkIndex);
            throw new CryptographicException(
                $"Decryption failed at chunk {_currentChunkIndex}: authentication tag mismatch. " +
                "The file may be corrupted or tampered with.", ex);
        }

        _currentDecryptedChunk = plaintext;
        _currentChunkDataLength = plaintext.Length;
        _chunkOffset = 0;
        _knownPlaintextLength = (_currentChunkIndex + 1) * (long)_blockSize;

        // 如果读取的密文少于块大小，说明是最后一块
        if (ciphertextRead < _blockSize)
        {
            // 调整已知明文长度
            _knownPlaintextLength = _knownPlaintextLength - _blockSize + ciphertextRead;
        }

        _logger?.LogTrace(
            "Decrypted chunk {ChunkIndex}: PlaintextLength={PlaintextLength}",
            _currentChunkIndex, _currentChunkDataLength);

        return true;
    }

    private bool _disposed;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            if (_masterKey is not null)
            {
                Array.Clear(_masterKey, 0, _masterKey.Length);
            }
            _currentDecryptedChunk = null;
        }
        base.Dispose(disposing);
    }
}
