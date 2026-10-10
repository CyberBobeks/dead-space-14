// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.DeadSpace.Ports.Jukebox;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.DeadSpace;

[TestFixture]
public sealed class JukeboxTransferTest
{
    [TestCase(12, 1)]
    [TestCase(16385, 7)]
    [TestCase(3500000, 1023)]
    public async Task SongSurvivesPartialReads(int bytes, int readSize)
    {
        var data = new byte[bytes];
        for (var i = 0; i < bytes; i++) data[i] = (byte)i;
        Encoding.ASCII.GetBytes("OggS").CopyTo(data, 0);
        using var encoded = new MemoryStream();
        await Codec.Write(encoded, "Тестовая запись", data, new NetEntity(42));
        using var fragmented = new PartialStream(encoded.ToArray(), readSize);
        var decoded = await Codec.Read(fragmented, bytes);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.TapeCreatorUid, Is.EqualTo(new NetEntity(42)));
            Assert.That(decoded.SongName, Is.EqualTo("Тестовая запись"));
            Assert.That(decoded.SongBytes.AsSpan().SequenceEqual(data), Is.True);
        });
    }

    [TestCase(0, 12)]
    [TestCase(513, 12)]
    [TestCase(1, 11)]
    [TestCase(1, 3500001)]
    [TestCase(1, int.MaxValue)]
    [TestCase(-1, 12)]
    [TestCase(1, -1)]
    public void InvalidSizesAreRejectedBeforeReadingPayload(int nameLength, int bytes)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), nameLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), bytes);
        using var stream = new MemoryStream(header);
        Assert.ThrowsAsync<InvalidDataException>(async () => await Codec.Read(stream, 3500000));
        Assert.That(stream.Position, Is.EqualTo(12));
    }

    [TestCase(0)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(15)]
    public async Task TruncatedUploadsAreRejected(int length)
    {
        using var encoded = new MemoryStream();
        await Codec.Write(encoded, "song", Encoding.ASCII.GetBytes("OggS01234567"));
        using var truncated = new PartialStream(encoded.ToArray()[..length], 1);
        Assert.ThrowsAsync<EndOfStreamException>(async () => await Codec.Read(truncated, 3500000));
    }

    [Test]
    public async Task NonOggUploadIsRejected()
    {
        using var encoded = new MemoryStream();
        await Codec.Write(encoded, "song", new byte[12]);
        encoded.Position = 0;
        Assert.ThrowsAsync<InvalidDataException>(async () => await Codec.Read(encoded, 3500000));
    }

    private sealed class Codec : JukeboxSongsSyncManager
    {
        public static Task Write(Stream stream, string name, byte[] data, NetEntity creator = default)
            => WriteSong(stream, name, data, creator);
        public static Task<JukeboxSongUploadRequest> Read(Stream stream, double maxBytes)
            => ReadSong(stream, maxBytes);
    }

    private sealed class PartialStream(byte[] data, int readSize) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], cancel);
    }
}
