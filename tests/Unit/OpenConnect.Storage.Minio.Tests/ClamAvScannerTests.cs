using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace OpenConnect.Storage.Minio.Tests;

public sealed class ClamAvScannerTests
{
    [Theory]
    [InlineData("stream: Eicar-Test-Signature FOUND\0")]
    [InlineData("stream: ERROR\0")]
    [InlineData("stream: OK")]
    [InlineData("other: OK\0")]
    public async Task InvalidOrMaliciousVerdictsFailClosed(string reply)
    {
        await using var stream = new DuplexStream(Encoding.ASCII.GetBytes(reply));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClamAvScanner.ScanStreamAsync(stream, new byte[] { 1 }, default));
    }

    [Fact]
    public async Task CleanVerdictUsesBoundedClamdFrames()
    {
        await using var stream = new DuplexStream("stream: OK\0"u8.ToArray());
        var content = new byte[65537];
        content[^1] = 42;
        await ClamAvScanner.ScanStreamAsync(stream, content, default);
        var written = stream.Written.ToArray();
        Assert.Equal("zINSTREAM\0"u8.ToArray(), written[..10]);
        Assert.Equal(65536, BinaryPrimitives.ReadInt32BigEndian(written.AsSpan(10, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(written.AsSpan(65550, 4)));
        Assert.Equal(42, written[65554]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(written.AsSpan(65555, 4)));
    }

    [Fact]
    public async Task OversizedReplyIsRejected()
    {
        await using var stream = new DuplexStream(Enumerable.Repeat((byte)'x', 4098).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClamAvScanner.ScanStreamAsync(stream, new byte[] { 1 }, default));
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        var scanner = new ClamAvScanner(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(new byte[] { 1 }, cancellation.Token));
    }

    [Fact]
    public async Task InvalidInputAndUnavailableSocketFailClosed()
    {
        var scanner = new ClamAvScanner(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanAsync(ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync(new byte[] { 1 }));
    }

    private sealed class DuplexStream(byte[] reply) : Stream
    {
        private readonly MemoryStream _input = new(reply);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Written.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _input.Dispose(); Written.Dispose(); } base.Dispose(disposing); }
    }
}
