using Replixer.Infrastructure;
using Replixer.Models;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using System.IO;

namespace Replixer.Services.Recording;

public class AudioRecordingService : IDisposable
{
    private readonly AppSettings _settings;

    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiCapture?         _micCapture;
    private WaveFileWriter?        _loopbackWriter;
    private WaveFileWriter?        _micWriter;

    private string? _loopbackTempPath;
    private string? _micTempPath;
    private string? _finalMp3Path;

    public bool    IsRecording       { get; private set; }
    public string  LastSavedFilePath { get; private set; } = string.Empty;
    public string? CurrentFilePath   => _finalMp3Path;
    public string? LastError         { get; private set; }

    public event Action<string>? RecordingCompleted;
    public event Action<string>? RecordingFailed;

    public AudioRecordingService(AppSettings settings)
    {
        _settings = settings;
        CleanupStaleTempFiles();
    }

    private static void CleanupStaleTempFiles()
    {
        try
        {
            string temp = Path.GetTempPath();
            foreach (var pattern in new[] { "ev_loopback_*.wav", "ev_mic_*.wav" })
            {
                foreach (var file in Directory.GetFiles(temp, pattern))
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
        }
        catch { }
    }

    public bool StartRecording(string appName)
    {
        if (IsRecording || _loopbackCapture != null || _micCapture != null) return false;

        LastError = null;
        try
        {
            string tempFolder = Path.GetTempPath();
            var    now        = DateTime.Now;
            string manager    = Sanitize(string.IsNullOrWhiteSpace(_settings.ManagerName) ? "Менеджер" : _settings.ManagerName);
            string platform   = Sanitize(PlatformHelper.ToFileName(appName));
            string baseName   = $"{manager}_{platform}_{now:yy.MM.dd}_{now:HH.mm}";

            string mp3Path = Path.Combine(tempFolder, $"{baseName}.mp3");
            if (File.Exists(mp3Path))
                mp3Path = Path.Combine(tempFolder, $"{baseName}_{now:ss}.mp3");

            string uid = now.ToString("HHmmss");
            _loopbackTempPath = Path.Combine(tempFolder, $"ev_loopback_{uid}.wav");
            _micTempPath      = Path.Combine(tempFolder, $"ev_mic_{uid}.wav");
            _finalMp3Path     = mp3Path;

            _loopbackCapture = new WasapiLoopbackCapture();
            _loopbackCapture.DataAvailable += (_, e) => _loopbackWriter?.Write(e.Buffer, 0, e.BytesRecorded);

            _micCapture = new WasapiCapture();
            _micCapture.DataAvailable += (_, e) => _micWriter?.Write(e.Buffer, 0, e.BytesRecorded);

            _loopbackWriter = new WaveFileWriter(_loopbackTempPath, _loopbackCapture.WaveFormat);
            _micWriter      = new WaveFileWriter(_micTempPath, _micCapture.WaveFormat);

            IsRecording = true;

            _loopbackCapture.StartRecording();
            _micCapture.StartRecording();

            Debug.WriteLine($"[Recording] Started — {appName}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Recording] Start failed: {ex.Message}");
            LastError = ex.Message;
            CleanupCaptures();
            return false;
        }
    }

    public async Task<string?> StopRecordingAsync()
    {
        if (!IsRecording) return null;
        IsRecording = false;

        await StopCapturesAsync().ConfigureAwait(false);

        _loopbackWriter?.Dispose(); _loopbackWriter = null;
        _micWriter?.Dispose();      _micWriter      = null;

        using var mixCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        string? path = await Task.Run(() => MixAndSaveToMp3(mixCts.Token)).ConfigureAwait(false);

        CleanupCaptures();

        if (path != null)
        {
            LastSavedFilePath = path;
            RecordingCompleted?.Invoke(path);
        }
        else
        {
            RecordingFailed?.Invoke("Не вдалося зберегти запис");
        }

        return path;
    }

    private async Task StopCapturesAsync()
    {
        var loopbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var micDone      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_loopbackCapture != null)
            _loopbackCapture.RecordingStopped += (_, _) => loopbackDone.TrySetResult();
        else
            loopbackDone.TrySetResult();

        if (_micCapture != null)
            _micCapture.RecordingStopped += (_, _) => micDone.TrySetResult();
        else
            micDone.TrySetResult();

        _loopbackCapture?.StopRecording();
        _micCapture?.StopRecording();

        var timeout = Task.Delay(TimeSpan.FromSeconds(3));
        await Task.WhenAll(
            Task.WhenAny(loopbackDone.Task, timeout),
            Task.WhenAny(micDone.Task,      timeout)
        ).ConfigureAwait(false);
    }

    private string? MixAndSaveToMp3(CancellationToken ct = default)
    {
        if (_loopbackTempPath is null || _micTempPath is null || _finalMp3Path is null)
            return null;

        try
        {
            using var loopbackReader = new AudioFileReader(_loopbackTempPath);
            using var micReader      = new AudioFileReader(_micTempPath);

            ISampleProvider loopback = loopbackReader.ToSampleProvider();
            ISampleProvider mic      = micReader.ToSampleProvider();

            if (loopback.WaveFormat.SampleRate != 44100)
                loopback = new WdlResamplingSampleProvider(loopback, 44100);
            if (mic.WaveFormat.SampleRate != 44100)
                mic = new WdlResamplingSampleProvider(mic, 44100);

            if (loopback.WaveFormat.Channels == 1)
                loopback = new MonoToStereoSampleProvider(loopback);
            if (mic.WaveFormat.Channels == 1)
                mic = new MonoToStereoSampleProvider(mic);

            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            mixer.AddMixerInput(loopback);
            mixer.AddMixerInput(mic);

            IWaveProvider pcm = new SampleToWaveProvider16(mixer);

            using var mp3 = new LameMP3FileWriter(_finalMp3Path, new WaveFormat(44100, 16, 2), 192);

            var buffer = new byte[44100 * 2 * 2];
            int bytesRead;
            while ((bytesRead = pcm.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                mp3.Write(buffer, 0, bytesRead);
            }

            Debug.WriteLine($"[Recording] Saved → {_finalMp3Path}");
            return _finalMp3Path;
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[Recording] Mix timed out after 5 minutes");
            LastError = "Час обробки аудіо вийшов";
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Recording] Mix failed: {ex.Message}");
            LastError = ex.Message;
            return null;
        }
        finally
        {
            SafeDelete(_loopbackTempPath);
            SafeDelete(_micTempPath);
        }
    }

    private void CleanupCaptures()
    {
        _loopbackCapture?.Dispose(); _loopbackCapture = null;
        _micCapture?.Dispose();      _micCapture      = null;
        _loopbackWriter?.Dispose();  _loopbackWriter  = null;
        _micWriter?.Dispose();       _micWriter       = null;
    }

    private static void SafeDelete(string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            try { File.Delete(path); } catch { }
    }

    private static string Sanitize(string name)
        => string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));

    public void Dispose()
    {
        if (IsRecording)
        {
            IsRecording = false;
            _loopbackCapture?.StopRecording();
            _micCapture?.StopRecording();
        }
        CleanupCaptures();
    }
}
