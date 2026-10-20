using ImageMagick;
using Sweetch.Core;
using Sweetch.Core.Backends;

namespace Sweetch.Tests;

public class RawTests
{
    [Theory]
    [InlineData("IMG_0001.CR3")]
    [InlineData("DSC_0001.NEF")]
    [InlineData("DSC00001.ARW")]
    [InlineData("IMG_0001.DNG")]
    [InlineData("DSCF0001.RAF")]
    public void Raw_files_are_image_inputs(string name)
    {
        var key = Formats.KeyOf(name);
        Assert.Contains(key, Formats.Raw);
        Assert.Contains(key, Formats.ImageInputs);
        // RAW can't be written back, so in-place edits are saved as JPG.
        Assert.Equal("jpg", ImageBackend.OutputFormatFor(key));
    }

    /// <summary>
    /// Real camera files are too big for the repository; CI downloads a few
    /// (see .github/workflows/build.yml) and points SWEETCH_RAW_SAMPLES at them.
    /// </summary>
    [Fact]
    public async Task Real_raw_samples_convert_to_jpg_and_png()
    {
        var dir = Environment.GetEnvironmentVariable("SWEETCH_RAW_SAMPLES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;
        var samples = Directory.GetFiles(dir).Where(f => Formats.Raw.Contains(Formats.KeyOf(f))).ToList();
        Assert.NotEmpty(samples);
        var outDir = Directory.CreateTempSubdirectory("sweetch-raw-").FullName;
        foreach (var raw in samples)
        {
            foreach (var target in new[] { "jpg", "png" })
            {
                var output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(raw) + "." + target);
                await ImageBackend.ConvertAsync(raw, output, target, new Settings(), CancellationToken.None);
                using var image = new MagickImage(output);
                // Developed full-size frame, not the small embedded thumbnail.
                Assert.True(Math.Max(image.Width, image.Height) >= 2000, $"{Path.GetFileName(raw)} → {image.Width}×{image.Height}");
            }
        }
    }
}
