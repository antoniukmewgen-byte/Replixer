using System.IO;
using System.Security.Cryptography;

namespace Replixer.Services.Upload;

// Wraps the Telegram MTProto session file so it's encrypted at rest with Windows DPAPI
// (bound to the current OS user account) instead of sitting on disk as a plaintext auth key.
// Passed to WTelegram.Client's sessionStore constructor param, which only ever calls
// Length/Read() once at startup to restore a session, then Write() to persist updates.
internal sealed class DpapiSessionStream : Stream
{
    private readonly string _path;
    private readonly MemoryStream _buffer;

    public DpapiSessionStream(string path)
    {
        _path = path;
        byte[] plaintext = [];

        if (File.Exists(path))
        {
            var onDisk = File.ReadAllBytes(path);
            if (onDisk.Length > 0)
            {
                try
                {
                    plaintext = ProtectedData.Unprotect(onDisk, null, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    // Pre-existing plaintext session from before DPAPI wrapping was added.
                    // Use it as-is; Persist() below will re-save it encrypted on the next Write().
                    plaintext = onDisk;
                }
            }
        }

        _buffer = new MemoryStream(plaintext.Length);
        _buffer.Write(plaintext, 0, plaintext.Length);
        _buffer.Position = 0;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => true;
    public override long Length   => _buffer.Length;

    public override long Position
    {
        get => _buffer.Position;
        set => _buffer.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _buffer.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);
    public override void SetLength(long value) => _buffer.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _buffer.Write(buffer, offset, count);
        Persist();
    }

    public override void Flush() { }

    private void Persist()
    {
        var encrypted = ProtectedData.Protect(_buffer.ToArray(), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, _path, overwrite: true);
    }
}
