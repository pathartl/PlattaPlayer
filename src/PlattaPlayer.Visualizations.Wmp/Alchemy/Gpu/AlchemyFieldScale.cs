using System;

namespace PlattaPlayer.Visualizations.Wmp.Alchemy.Gpu;

/// <summary>
/// How the GPU path maps the original's field onto the window.
///
/// The original always renders a fixed 640x480 field and stretches it to the window, so on anything
/// but a 4:3 window every shape is squashed. The GPU path renders at the window's own resolution
/// instead, with every length scaled by one uniform factor <see cref="Scale"/>, so shapes keep their
/// size relative to a 640x480 field AND keep their proportions. A window wider than 4:3 gets a WIDER
/// field (still 480 units tall), and a taller one a taller field (still 640 wide). The effects already
/// derive their geometry from the field size, so they spread across the extra room the same way they
/// would if the original had been handed a field of that shape.
/// </summary>
public readonly record struct AlchemyFieldScale(int FieldWidth, int FieldHeight, int DeviceWidth, int DeviceHeight, double Scale)
{
    public const int BaseWidth = 640;
    public const int BaseHeight = 480;

    /// <summary>
    /// True when the window is exactly the original field. The shaders then run the original's exact
    /// integer arithmetic, which is what lets the harness check them against the CPU port.
    /// </summary>
    public bool IsExact => DeviceWidth == FieldWidth && DeviceHeight == FieldHeight;

    public static AlchemyFieldScale For(int deviceWidth, int deviceHeight)
    {
        deviceWidth = Math.Max(1, deviceWidth);
        deviceHeight = Math.Max(1, deviceHeight);
        if (deviceWidth == BaseWidth && deviceHeight == BaseHeight)
            return new AlchemyFieldScale(BaseWidth, BaseHeight, deviceWidth, deviceHeight, 1.0);

        int fieldWidth, fieldHeight;
        double scale;
        if ((long)deviceWidth * BaseHeight >= (long)deviceHeight * BaseWidth)
        {
            scale = deviceHeight / (double)BaseHeight;
            fieldHeight = BaseHeight;
            fieldWidth = (int)Math.Round(deviceWidth / scale);
        }
        else
        {
            scale = deviceWidth / (double)BaseWidth;
            fieldWidth = BaseWidth;
            fieldHeight = (int)Math.Round(deviceHeight / scale);
        }

        return new AlchemyFieldScale(Math.Max(8, fieldWidth), Math.Max(8, fieldHeight), deviceWidth, deviceHeight, scale);
    }
}
