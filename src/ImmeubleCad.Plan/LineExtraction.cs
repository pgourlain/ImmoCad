// Adapted from CinnabarSharp (https://github.com/pgourlain/CinnabarSharp), see PROVENANCE.md.
namespace ImmeubleCad.Plan;

/// <summary>What <see cref="LineExtraction"/> keeps.</summary>
public enum LineMode
{
    /// <summary>Thick strokes (the walls of a plan); thin ones (dimensions, text, hatching) are dropped.</summary>
    Walls,

    /// <summary>The opposite: only the strokes thinner than the minimum thickness (dimensions, furniture, text).</summary>
    ThinLines,
}

/// <summary>
/// Settings of the line extraction. <paramref name="Threshold"/> 0 means automatic (Otsu); <paramref name="MinThickness"/>
/// is the width in pixels from which a stroke counts as thick; <paramref name="BridgeGap"/> closes breaks up to that many
/// pixels; components smaller than <paramref name="MinArea"/> pixels are dropped; <paramref name="LightLines"/> looks for
/// light strokes on a dark background instead of dark on light.
/// </summary>
public sealed record LineExtractionOptions(LineMode Mode = LineMode.Walls, int Threshold = 0, int MinThickness = 5,
    int BridgeGap = 0, int MinArea = 20, bool LightLines = false);

/// <summary>
/// Separates the lines of a drawing (the walls of a floor plan) from the rest, as a 0/255 mask the size of the image.
/// Integer-only and deterministic: grayscale, threshold, a square morphological opening of about one wall thickness
/// (removes the thin strokes), an optional closing (bridges small breaks) and the removal of small specks.
/// </summary>
public static class LineExtraction
{
    /// <summary>Otsu's threshold: the level that best separates dark from light (a pixel is dark when below it).</summary>
    public static int OtsuThreshold(ReadOnlySpan<byte> gray)
    {
        Span<long> histogram = stackalloc long[256];
        foreach (var v in gray)
            histogram[v]++;
        return OtsuLevel(histogram);
    }

    private static int OtsuLevel(ReadOnlySpan<long> histogram)
    {
        long total = 0, sumAll = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            total += histogram[i];
            sumAll += i * histogram[i];
        }
        if (total == 0)
            return 1;
        long weightBack = 0, sumBack = 0;
        double bestVariance = -1;
        int first = 0, last = 0;
        for (var t = 0; t < histogram.Length; t++)
        {
            weightBack += histogram[t];
            if (weightBack == 0)
                continue;
            var weightFront = total - weightBack;
            if (weightFront == 0)
                break;
            sumBack += t * histogram[t];
            var meanBack = (double)sumBack / weightBack;
            var meanFront = (double)(sumAll - sumBack) / weightFront;
            var variance = (double)weightBack * weightFront * (meanBack - meanFront) * (meanBack - meanFront);
            // Levels with no pixel between them tie exactly: take the middle of the plateau, not its first level.
            if (variance > bestVariance)
            {
                bestVariance = variance;
                first = last = t;
            }
            else if (variance == bestVariance)
                last = t;
        }
        return Math.Clamp((first + last) / 2 + 1, 1, 255); // pixels strictly below this level are dark
    }

    /// <summary>The ink of the picture: 1 where a line is, 0 elsewhere.</summary>
    public static byte[] Binarize(byte[] gray, int threshold, bool lightLines)
    {
        var level = threshold > 0 ? Math.Clamp(threshold, 1, 255) : OtsuThreshold(gray);
        var ink = new byte[gray.Length];
        for (var i = 0; i < gray.Length; i++)
            ink[i] = (byte)((lightLines ? gray[i] >= level : gray[i] < level) ? 1 : 0);
        return ink;
    }

    /// <summary>Longest side the thickness analysis works on, and the largest thickness it looks for.</summary>
    private const int SuggestSize = 800, SuggestMaxThickness = 40;

    /// <summary>
    /// A minimum thickness that keeps the thick strokes of this picture and drops the thin ones (the Auto button). The
    /// picture is measured on a reduced copy by granulometry: the ink left after an opening of size k is the ink covered by a
    /// k × k square, so the loss between k and k + 1 is the amount of ink that is exactly k thick (corners and junctions
    /// count as thick as the walls that make them). The thickness holding the most ink (from 2 up, so that 1-pixel lines never
    /// decide) is the usual wall; strokes at least 40% as thick are kept (partition walls are thinner than outer ones, while
    /// furniture, dimensions and text are much thinner). Between 2 and 100.
    /// </summary>
    public static int SuggestThickness(byte[] gray, int width, int height, LineExtractionOptions? options = null)
    {
        options ??= new LineExtractionOptions();
        var full = Binarize(gray, options.Threshold, options.LightLines);

        // Reduced copy: a block is ink when at least half of it is.
        var factor = Math.Max(1, (Math.Max(width, height) + SuggestSize - 1) / SuggestSize);
        var (w, h) = ((width + factor - 1) / factor, (height + factor - 1) / factor);
        var ink = new byte[w * h];
        if (factor == 1)
            ink = full;
        else
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    int count = 0, total = 0;
                    for (var j = y * factor; j < Math.Min(height, (y + 1) * factor); j++)
                        for (var i = x * factor; i < Math.Min(width, (x + 1) * factor); i++, total++)
                            count += full[j * width + i];
                    ink[y * w + x] = (byte)(count * 2 >= total ? 1 : 0);
                }

        var remaining = new long[SuggestMaxThickness + 2];
        remaining[1] = ink.Count(v => v != 0);
        for (var k = 2; k <= SuggestMaxThickness + 1 && remaining[k - 1] > 0; k++)
        {
            var opened = (byte[])ink.Clone();
            Erode(opened, w, h, k, CancellationToken.None);
            Dilate(opened, w, h, k, reflected: true, CancellationToken.None);
            remaining[k] = opened.Count(v => v != 0);
        }

        // Ink that is exactly t thick: what an opening of size t keeps and one of size t + 1 loses.
        long best = 0;
        var usual = 0;
        for (var t = 2; t <= SuggestMaxThickness; t++)
        {
            var amount = remaining[t] - remaining[t + 1];
            if (amount > best)
                (best, usual) = (amount, t);
        }
        if (usual == 0)
            return 2;
        return Math.Clamp((int)Math.Ceiling(usual * 0.4) * factor, 2, 100);
    }

    /// <summary>
    /// The lines of the picture as a 0/255 mask.
    /// </summary>
    public static byte[] Extract(byte[] gray, int width, int height, LineExtractionOptions options,
        CancellationToken cancellation = default)
    {
        var size = Math.Clamp(options.MinThickness, 1, 255);
        var ink = Binarize(gray, options.Threshold, options.LightLines);
        cancellation.ThrowIfCancellationRequested();

        // Opening: erosion then dilation with the same square; what is thinner than the square disappears.
        var opened = (byte[])ink.Clone();
        if (size > 1)
        {
            Erode(opened, width, height, size, cancellation);
            Dilate(opened, width, height, size, reflected: true, cancellation);
        }

        byte[] mask;
        if (options.Mode == LineMode.Walls)
        {
            mask = opened;
            if (options.BridgeGap > 0)
            {
                var gap = Math.Clamp(options.BridgeGap, 0, 255) + 1;
                Dilate(mask, width, height, gap, reflected: false, cancellation);
                Erode(mask, width, height, gap, cancellation, reflected: true);
            }
        }
        else
        {
            mask = ink;
            for (var i = 0; i < mask.Length; i++)
                if (opened[i] != 0)
                    mask[i] = 0;
        }

        RemoveSmallComponents(mask, width, height, options.MinArea, cancellation);

        var result = new byte[mask.Length];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)(mask[i] != 0 ? 255 : 0);
        return result;
    }

    // ---- Morphology: separable min/max filters in O(n), with 0/1 pixels ----

    /// <summary>Erosion by a size × size square. Outside the image counts as ink, so walls touching the edge keep their end.</summary>
    private static void Erode(byte[] data, int width, int height, int size, CancellationToken cancellation, bool reflected = false)
    {
        if (size <= 1)
            return;
        Filter(data, width, height, size, erode: true, reflected, cancellation);
    }

    /// <summary>Dilation by a size × size square. Outside the image counts as empty.</summary>
    private static void Dilate(byte[] data, int width, int height, int size, bool reflected, CancellationToken cancellation)
    {
        if (size <= 1)
            return;
        Filter(data, width, height, size, erode: false, reflected, cancellation);
    }

    private static void Filter(byte[] data, int width, int height, int size, bool erode, bool reflected, CancellationToken cancellation)
    {
        var line = new byte[Math.Max(width, height)];
        var output = new byte[line.Length];
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellation.ThrowIfCancellationRequested();
            FilterLine(data, y * width, 1, width, size, erode, reflected, line, output);
        }
        for (var x = 0; x < width; x++)
        {
            if ((x & 63) == 0)
                cancellation.ThrowIfCancellationRequested();
            FilterLine(data, x, width, height, size, erode, reflected, line, output);
        }
    }

    /// <summary>
    /// Filters one row or column. The window of pixel x covers [x - before, x + after] with before + after + 1 = size;
    /// <paramref name="reflected"/> swaps the two (the mirrored element an opening or closing needs for even sizes).
    /// </summary>
    private static void FilterLine(byte[] data, int start, int step, int length, int size, bool erode, bool reflected,
        byte[] line, byte[] output)
    {
        for (var i = 0; i < length; i++)
            line[i] = data[start + i * step];
        var before = size / 2;
        var after = size - 1 - before;
        if (reflected)
            (before, after) = (after, before);

        if (erode)
        {
            // Run of ink ending at the window's last pixel; the virtual pixels before the line count as ink.
            var run = before;
            for (var e = 0; e < length + after; e++)
            {
                run = (e < length ? line[e] : (byte)1) != 0 ? run + 1 : 0;
                var x = e - after;
                if (x >= 0)
                    output[x] = (byte)(run >= size ? 1 : 0);
            }
        }
        else
        {
            var last = int.MinValue / 2;
            for (var e = 0; e < length + after; e++)
            {
                if (e < length && line[e] != 0)
                    last = e;
                var x = e - after;
                if (x >= 0)
                    output[x] = (byte)(last >= x - before ? 1 : 0);
            }
        }
        for (var i = 0; i < length; i++)
            data[start + i * step] = output[i];
    }

    /// <summary>Clears the 8-connected components of ink smaller than <paramref name="minArea"/> pixels.</summary>
    private static void RemoveSmallComponents(byte[] mask, int width, int height, int minArea, CancellationToken cancellation)
    {
        if (minArea <= 1)
            return;
        var stack = new List<int>();
        var members = new List<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (mask[start] != 1)
                continue;
            if ((start & 0xFFFF) == 0)
                cancellation.ThrowIfCancellationRequested();
            members.Clear();
            stack.Add(start);
            mask[start] = 2;
            while (stack.Count > 0)
            {
                var p = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                members.Add(p);
                int px = p % width, py = p / width;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;
                        var n = ny * width + nx;
                        if (mask[n] != 1)
                            continue;
                        mask[n] = 2;
                        stack.Add(n);
                    }
            }
            // 2 = kept component, 0 = removed one.
            var value = (byte)(members.Count >= minArea ? 2 : 0);
            foreach (var m in members)
                mask[m] = value;
        }
        for (var i = 0; i < mask.Length; i++)
            if (mask[i] == 2)
                mask[i] = 1;
    }
}
