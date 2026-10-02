using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FreeSpace.Desktop;

/// <summary>Arrow geometry for a transfer row: up for uploads, down for downloads.</summary>
public static class TransferIcons
{
    private static readonly Geometry Up = Geometry.Parse("M12 4l6 6.5h-4V17h-4v-6.5H6z M6 19h12v2H6z");
    private static readonly Geometry Down = Geometry.Parse("M10 4h4v6.5h4L12 17l-6-6.5h4z M6 19h12v2H6z");

    public static readonly IValueConverter Direction = new FuncValueConverter<bool, Geometry>(isUpload => isUpload ? Up : Down);
}
