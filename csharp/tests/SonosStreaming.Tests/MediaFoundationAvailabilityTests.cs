using System.Runtime.InteropServices;
using FluentAssertions;
using SonosStreaming.Core.Audio;
using Xunit;

namespace SonosStreaming.Tests;

// Windows N/KN editions without the Media Feature Pack, and Windows Server
// without the Media Foundation feature, have no MFPlat.dll. Those failures
// should read as "Media Foundation is missing", not as a raw interop error.
public sealed class MediaFoundationAvailabilityTests
{
    [Fact]
    public void MissingMfPlat_IsRecognised()
    {
        MediaFoundationAvailability.IsMissing(new DllNotFoundException("Unable to load DLL 'MFPlat.dll'")).Should().BeTrue();
    }

    [Fact]
    public void UnregisteredMftClass_IsRecognised()
    {
        var classNotRegistered = new COMException("Class not registered", unchecked((int)0x80040154));
        MediaFoundationAvailability.IsMissing(classNotRegistered).Should().BeTrue();
    }

    [Fact]
    public void AnUnsupportedMediaType_IsNotMistakenForMissingMediaFoundation()
    {
        var invalidMediaType = new COMException("The data specified for the media type is invalid", unchecked((int)0xC00D36B4));
        MediaFoundationAvailability.IsMissing(invalidMediaType).Should().BeFalse();
        MediaFoundationAvailability.IsMissing(new InvalidOperationException()).Should().BeFalse();
    }
}
