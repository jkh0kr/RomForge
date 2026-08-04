using System.IO.Compression;

namespace Patch.Core.Services;

public sealed class ZipEntryLengthStream(ZipArchiveEntry entry) : Stream
{
    private readonly Stream _inner = entry.Open();
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length { get; } = entry.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("zip 엔트리 스트림은 순차 읽기만 지원합니다.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);

        _position += read;

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken);

        _position += read;

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("zip 엔트리 스트림은 Seek을 지원하지 않습니다.");

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}