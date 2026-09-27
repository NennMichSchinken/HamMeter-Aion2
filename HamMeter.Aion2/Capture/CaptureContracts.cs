namespace HamMeter.Capture;

// Receives the in-order TCP payload of the game streams.
public interface IStreamSink
{
    void AddData(string streamKey, byte[] payload, long receivedAtUnixMs);

    void ClearStream(string streamKey);
}

// A packet source for HamMeter's own pipeline.
public interface IGameCapture : IDisposable
{
    bool IsCapturing { get; }

    // Non-null once a game stream was confirmed.
    string? DeviceName { get; }

    // Aion is connected but no packet of it arrives (typically the firewall).
    bool LooksBlocked { get; }

    void StartCapture();

    void StopCapture();
}
