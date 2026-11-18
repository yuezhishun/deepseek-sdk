using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;

namespace DeepSeek;

internal static class DeepSeekUpload
{
    internal static void ValidateStream(Stream stream)
    {
        ArgumentGuard.ThrowIfNull(stream, nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("The stream must be readable.", nameof(stream));
    }

    internal static void ValidateFileName(string fileName)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(fileName, nameof(fileName));
        if (Encoding.UTF8.GetByteCount(fileName) > 512)
        {
            throw new ArgumentException("A file name must not exceed 512 UTF-8 bytes.", nameof(fileName));
        }
    }

    internal static void AddFile(MultiPartFormContent form, Stream data, string fileName)
        => form.Add("file", new FileBinaryContent(data, "application/octet-stream") { Filename = fileName });

    internal static MemoryStream Read(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        ValidateStream(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.CanSeek && stream.Length - stream.Position > maximumBytes) throw SizeException(maximumBytes);
        var data = new MemoryStream();
        var buffer = new byte[81920];
        while (data.Length < maximumBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes - (int)data.Length));
            if (count == 0)
            {
                break;
            }

            AppendToMemory(data, buffer, count, maximumBytes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length == maximumBytes && stream.ReadByte() != -1)
        {
            throw SizeException(maximumBytes);
        }

        data.Position = 0;
        return data;
    }

    internal static async Task<MemoryStream> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        ValidateStream(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.CanSeek && stream.Length - stream.Position > maximumBytes) throw SizeException(maximumBytes);
        var data = new MemoryStream();
        var buffer = new byte[81920];
        while (data.Length < maximumBytes)
        {
            var count = await stream.ReadAsync(
                buffer,
                0,
                Math.Min(buffer.Length, maximumBytes - (int)data.Length),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            AppendToMemory(data, buffer, count, maximumBytes);
        }

        if (data.Length == maximumBytes)
        {
            var extraByte = new byte[1];
            if (await stream.ReadAsync(extraByte, 0, 1, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw SizeException(maximumBytes);
            }
        }

        data.Position = 0;
        return data;
    }

    private static InvalidDataException SizeException(int maximumBytes)
        => new("The file exceeds the maximum upload size of " + maximumBytes / (1024 * 1024) + " MiB.");

    private static void AppendToMemory(MemoryStream data, byte[] buffer, int count, int maximumBytes)
    {
        var requiredCapacity = checked(data.Length + count);
        if (requiredCapacity > data.Capacity)
        {
            var doubledCapacity = Math.Min((long)data.Capacity * 2, maximumBytes);
            var capacity = Math.Max(requiredCapacity, Math.Max(doubledCapacity, buffer.Length));
            data.Capacity = (int)Math.Min(capacity, maximumBytes);
        }

        data.Write(buffer, 0, count);
    }

}
