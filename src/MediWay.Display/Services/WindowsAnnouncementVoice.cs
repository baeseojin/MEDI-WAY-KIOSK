using System.Speech.Synthesis;

namespace MediWay.Display.Services;

public sealed class WindowsAnnouncementVoice : IAnnouncementVoice
{
    private readonly SpeechSynthesizer? synthesizer;
    private readonly object gate = new();
    public bool IsAvailable => synthesizer is not null;
    public string Description { get; }

    // A wave path supports engine verification without playing audio through the speakers.
    public WindowsAnnouncementVoice(string? wavePath = null)
    {
        SpeechSynthesizer? candidate = null;
        try
        {
            candidate = new SpeechSynthesizer();
            var voice = candidate.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "ko");
            if (voice is null) { Description = "한국어 TTS 음성이 없습니다. 화면 안내를 이용해 주세요."; candidate.Dispose(); return; }
            candidate.SelectVoice(voice.VoiceInfo.Name);
            candidate.Rate = -1; candidate.Volume = 80;
            if (wavePath is null) candidate.SetOutputToDefaultAudioDevice(); else candidate.SetOutputToWaveFile(wavePath);
            synthesizer = candidate; Description = "음성 안내 준비됨 · " + voice.VoiceInfo.Name;
        }
        catch (Exception)
        {
            candidate?.Dispose(); Description = "TTS를 준비하지 못했습니다. 화면 안내를 이용해 주세요.";
        }
    }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (synthesizer is null) return;
        ct.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, SpeakCompletedEventArgs e)
        {
            if (e.Error is not null) done.TrySetException(e.Error);
            else if (e.Cancelled) done.TrySetCanceled();
            else done.TrySetResult();
        }
        synthesizer.SpeakCompleted += Completed;
        using var registration = ct.Register(() => { lock (gate) synthesizer.SpeakAsyncCancelAll(); });
        try
        {
            lock (gate) { ct.ThrowIfCancellationRequested(); synthesizer.SpeakAsync(text); }
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        finally { lock (gate) { synthesizer.SpeakAsyncCancelAll(); synthesizer.SpeakCompleted -= Completed; } }
    }
    public void Dispose() { synthesizer?.SpeakAsyncCancelAll(); synthesizer?.Dispose(); }
}
