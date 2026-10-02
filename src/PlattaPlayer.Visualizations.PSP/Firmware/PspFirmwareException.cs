namespace PlattaPlayer.Visualizations.PSP.Firmware;

/// <summary>An expected failure while locating or extracting the firmware assets. The message is user-facing.</summary>
public sealed class PspFirmwareException : Exception
{
    public PspFirmwareException(string message) : base(message)
    {
    }

    public PspFirmwareException(string message, Exception inner) : base(message, inner)
    {
    }
}
