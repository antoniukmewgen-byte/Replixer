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

    public bool   IsRecording      { get; private set; }
    public string LastSavedFilePath { get; private set; } = string.Empty;

    public event Action<string>? RecordingCompleted; // saved file path
    public event Action<string>? RecordingFailed;    // error message

    public AudioRecordingService(AppSettings settings)
    {
        _settings = settings;
    }

    // ── Start ─────────────────────────────────────────────────────────────────

    public bool StartRecording(string appName)
    {
        // Guard against starting while a previous StopRecordingAsync is still tearing down:
        // IsRecording is cleared early, but captures are still alive until CleanupCaptures().
        if (IsRecording || _loopbackCapture != null || _micCapture != null) return false;

        try
        {
            string tempFolder = Path.GetTempPath();
            var    now        = DateTime.Now;
            string manager    = Sanitize(string.IsNullOrWhiteSpace(_settings.ManagerName) ? "Менеджер" : _settings.ManagerName);
            string platform   = Sanitize(LocalizePlatform(appName));
            string baseName   = $"{manager}_{platform}_{now:yy.MM.dd}_{now:HH.mm}";

            // avoid collision when two recordings start in the same minute
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
            CleanupCaptures();
            return false;
        }
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    public async Task<string?> StopRecordingAsync()
    {
        if (!IsRecording) return null;
        IsRecording = false;

        // Signal captures to stop and wait for RecordingStopped events
        // (avoids Thread.Sleep — lets WASAPI flush naturally)
        await StopCapturesAsync().ConfigureAwait(false);

        // Flush WAV writers to disk before reading them
        _loopbackWriter?.Dispose(); _loopbackWriter = null;
        _micWriter?.Dispose();      _micWriter      = null;

        // Mix + encode on background thread so UI stays responsive
        string? path = await Task.Run(MixAndSaveToMp3).ConfigureAwait(false);

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

        // Timeout of 3s in case a capture never fires RecordingStopped
        var timeout = Task.Delay(TimeSpan.FromSeconds(3));
        await Task.WhenAll(
            Task.WhenAny(loopbackDone.Task, timeout),
            Task.WhenAny(micDone.Task,      timeout)
        ).ConfigureAwait(false);
    }

    // ── Mix & encode ──────────────────────────────────────────────────────────

    private string? MixAndSaveToMp3()
    {
        if (_loopbackTempPath is null || _micTempPath is null || _finalMp3Path is null)
            return null;

        try
        {
            using var loopbackReader = new AudioFileReader(_loopbackTempPath);
            using var micReader      = new AudioFileReader(_micTempPath);

            ISampleProvider loopback = loopbackReader.ToSampleProvider();
            ISampleProvider mic      = micReader.ToSampleProvider();

            // Normalise to 44100 Hz
            if (loopback.WaveFormat.SampleRate != 44100)
                loopback = new WdlResamplingSampleProvider(loopback, 44100);
            if (mic.WaveFormat.SampleRate != 44100)
                mic = new WdlResamplingSampleProvider(mic, 44100);

            // Normalise to stereo
            if (loopback.WaveFormat.Channels == 1)
                loopback = new MonoToStereoSampleProvider(loopback);
            if (mic.WaveFormat.Channels == 1)
                mic = new MonoToStereoSampleProvider(mic);

            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
            mixer.AddMixerInput(loopback);
            mixer.AddMixerInput(mic);

            // SampleToWaveProvider16 handles float→int16 correctly
            IWaveProvider pcm = new SampleToWaveProvider16(mixer);

            // temp folder always exists — no need to create it

            using var mp3 = new LameMP3FileWriter(_finalMp3Path, new WaveFormat(44100, 16, 2), 192);

            var buffer = new byte[44100 * 2 * 2]; // ~1 s of 44100 Hz stereo 16-bit
            int bytesRead;
            while ((bytesRead = pcm.Read(buffer, 0, buffer.Length)) > 0)
                mp3.Write(buffer, 0, bytesRead);

            Debug.WriteLine($"[Recording] Saved → {_finalMp3Path}");
            return _finalMp3Path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Recording] Mix failed: {ex.Message}");
            return null;
        }
        finally
        {
            SafeDelete(_loopbackTempPath);
            SafeDelete(_micTempPath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static string LocalizePlatform(string name) => name switch
    {
        "Telegram"  => "Телеграм",
        "WhatsApp"  => "WhatsApp",
        "Viber"     => "Viber",
        "Ringostat" => "Ringostat",
        _           => name,
    };

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
