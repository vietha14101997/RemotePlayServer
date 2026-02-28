#nullable enable
using System;
using System.Threading.Tasks;

namespace RemotePlayServer.Core.Interfaces;

/// <summary>
/// Abstraction for the streaming transport layer (WebRTC, etc.).
/// </summary>
public interface IStreamTransport : IDisposable
{
    Task<bool> StartAsync();
    void SendEncodedFrame(byte[] nalData, bool isKeyFrame, long pts, int monitorIndex);
    void Stop();
}
