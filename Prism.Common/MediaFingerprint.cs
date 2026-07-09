using System.Security.Cryptography;

namespace Prism.Common;

/// <summary>
/// Быстрый отпечаток медиафайла для идентификации содержимого независимо от пути
/// и машины: размер файла + SHA-256 первого и последнего блоков. Читается за
/// миллисекунды независимо от размера файла (в отличие от хеша всего контента), а
/// совпадение и размера, и краёв у двух разных реальных файлов практически
/// невозможно. Считается одним и тем же кодом в хосте и в лаунчере, поэтому
/// отпечатки сопоставимы между процессами и машинами.
/// </summary>
/// <param name="Size">Размер файла в байтах (для хоста — быстрый пред-фильтр).</param>
/// <param name="Hash">SHA-256 краёв файла в нижнем регистре hex.</param>
public sealed record MediaFingerprint(long Size, string Hash)
{
    public override string ToString() => $"{Size}-{Hash}";
}

public static class MediaFingerprinter
{
    // Блок с каждого края файла: 64 КБ хватает, чтобы захватить контейнерные
    // заголовки/индексы; читать больше смысла нет.
    private const int ChunkSize = 64 * 1024;

    /// <summary>Считает отпечаток файла по пути. Пробрасывает обычные IO-исключения.</summary>
    public static MediaFingerprint Compute(string path)
    {
        using var stream = File.OpenRead(path);
        return Compute(stream, stream.Length);
    }

    /// <summary>Считает отпечаток по открытому потоку известной длины (удобно для тестов).</summary>
    public static MediaFingerprint Compute(Stream stream, long size)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (size <= 2L * ChunkSize)
        {
            // Мелкий файл — хешируем целиком (край в край).
            stream.Seek(0, SeekOrigin.Begin);
            Append(sha, stream, size);
        }
        else
        {
            stream.Seek(0, SeekOrigin.Begin);
            Append(sha, stream, ChunkSize);
            stream.Seek(-ChunkSize, SeekOrigin.End);
            Append(sha, stream, ChunkSize);
        }

        return new MediaFingerprint(size, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    private static void Append(IncrementalHash sha, Stream stream, long count)
    {
        var buffer = new byte[(int)Math.Min(ChunkSize, count)];
        var remaining = count;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0) break; // файл короче ожидаемого — хеш всё равно стабилен
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }
}
