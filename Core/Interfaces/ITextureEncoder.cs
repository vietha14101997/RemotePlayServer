#nullable enable
using System;
using Vortice.Direct3D11;
namespace RemotePlayServer.Core.Interfaces;

/// <summary>
/// Composite interface for hardware texture encoders (AMF, NVENC, QSV).
/// Composes IVideoEncoder + IAdaptiveEncoder + IBgraEncoder for backward compatibility.
/// New code should prefer the focused interfaces from Core.Interfaces.
/// </summary>
public interface ITextureEncoder : IVideoEncoder, IAdaptiveEncoder, IBgraEncoder
{
}
