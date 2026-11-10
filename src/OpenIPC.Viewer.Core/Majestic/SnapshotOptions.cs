namespace OpenIPC.Viewer.Core.Majestic;

// Width/Height null = native sensor resolution. Majestic resizes server-side
// on /image.jpg, which is what makes it cheap enough for grid thumbnails.
public sealed record MajesticSnapshotOptions(int? Width = null, int? Height = null)
{
    // A grid cell is only a few hundred CSS pixels wide, yet the grid used to
    // ask every camera for a native 1080p/4MP JPEG (commonly 0.5–1.5 MB) on
    // every refresh. N tiles refreshing together is what produces the
    // multi-MB/s spikes. Majestic scales BEFORE JPEG-encoding, so requesting
    // the size we actually display shrinks the bytes on the wire by roughly
    // an order of magnitude with no visible change in the tile.
    public const int MinGridWidth = 320;
    public const int MaxGridWidth = 1920;

    /// <summary>
    /// 0 means "native resolution" (saved snapshots, single-camera previews).
    /// Anything else is clamped to a sane range so a bad layout value or a
    /// crafted query string can't ask a camera to encode a giant image.
    /// </summary>
    public static int ClampWidth(int width)
    {
        if (width <= 0) return 0;
        if (width < MinGridWidth) return MinGridWidth;
        if (width > MaxGridWidth) return MaxGridWidth;
        return width;
    }

    /// <summary>Options for a still that will be shown in a grid cell.</summary>
    public static MajesticSnapshotOptions ForGrid(int maxWidth)
    {
        var w = ClampWidth(maxWidth);
        return w == 0 ? new MajesticSnapshotOptions() : new MajesticSnapshotOptions(Width: w);
    }

    /// <summary>
    /// Desired still width for an N×N layout. 1×1 fills the window, so keep it
    /// sharp; 2×2 and denser never need more than 960/640 px. Web Grid.tsx
    /// mirrors this ladder for its <c>?w=</c> query parameter.
    /// </summary>
    public static int ForGridSize(int layoutSize)
    {
        if (layoutSize <= 1) return MaxGridWidth;
        if (layoutSize == 2) return 960;
        return 640;
    }
}
