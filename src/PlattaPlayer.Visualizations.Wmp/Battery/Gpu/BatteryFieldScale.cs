namespace PlattaPlayer.Visualizations.Wmp.Battery.Gpu;

/// <summary>
/// How the GPU path maps Battery's field onto the window: <see cref="Alchemy.Gpu.AlchemyFieldScale"/> with
/// the 384x288 base.
///
/// The original renders a fixed 384x288 field and stretches it to the window. The GPU path renders at
/// the window's own resolution with one uniform factor <see cref="Scale"/> = min(W/384, H/288), so shapes
/// keep their size relative to the original field AND their proportions. A window wider than 4:3 gets a
/// WIDER field (still 288 units tall) and a taller one a taller field. <see cref="BatteryCore"/> runs at
/// that field size, which is the original's own resolution-change path, so the effects spread across the
/// extra room the way they would had the original been given a field of that shape.
/// </summary>
public readonly record struct BatteryFieldScale(int FieldWidth, int FieldHeight, int DeviceWidth, int DeviceHeight, double Scale)
{
    public const int BaseWidth = BatteryCore.FieldWidth;
    public const int BaseHeight = BatteryCore.FieldHeight;

    /// <summary>
    /// True when the window is exactly the original field. The GPU then gathers through the original's
    /// own tables and runs its exact blur, which is what the harness checks against the CPU port.
    /// </summary>
    public bool IsExact => DeviceWidth == FieldWidth && DeviceHeight == FieldHeight;

    public static BatteryFieldScale For(int deviceWidth, int deviceHeight)
    {
        deviceWidth = Math.Max(1, deviceWidth);
        deviceHeight = Math.Max(1, deviceHeight);
        if (deviceWidth == BaseWidth && deviceHeight == BaseHeight)
            return new BatteryFieldScale(BaseWidth, BaseHeight, deviceWidth, deviceHeight, 1.0);

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

        return new BatteryFieldScale(Math.Max(16, fieldWidth), Math.Max(16, fieldHeight), deviceWidth, deviceHeight, scale);
    }
}
