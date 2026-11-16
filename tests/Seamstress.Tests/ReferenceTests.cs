using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Seamstress.Core.Editing;
using Seamstress.Core.IO;
using Seamstress.Core.Model;

namespace Seamstress.Tests;

public class ReferenceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Seamstress.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static ReferenceImage Add(Workspace ws, Vector2? position = null, float width = 40, float height = 20)
        => ws.AddReference("/tmp/reference.png", "Reference", position ?? Vector2.Zero, width, height);

    [Fact]
    public void ReferencesSurviveRoundTripAndKeepPortablePaths()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"seamstress-refs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "refs"));
        string image = Path.Combine(folder, "refs", "front.png");
        File.WriteAllBytes(image, Array.Empty<byte>());
        try
        {
            var document = new Document();
            document.References.Add(new ReferenceImage
            {
                Name = "Front view",
                Path = image,
                Position = new Vector2(12.5f, -3),
                Width = 42,
                Height = 31,
                Rotation = 15,
                Opacity = 0.4f,
                Visible = false,
                Locked = true,
            });

            string json = GarmentFile.Serialize(document, folder);
            Assert.Contains("\"references\"", json);
            Assert.Contains("\"path\": \"refs/front.png\"", json);
            Assert.Contains("\"visible\": false", json);
            Assert.Contains("\"locked\": true", json);

            var loaded = GarmentFile.Deserialize(json, folder);
            var reference = Assert.Single(loaded.References);
            Assert.Equal(image, reference.Path);
            Assert.Equal("Front view", reference.Name);
            Assert.Equal(new Vector2(12.5f, -3), reference.Position);
            Assert.Equal(42f, reference.Width);
            Assert.Equal(31f, reference.Height);
            Assert.Equal(15f, reference.Rotation);
            Assert.Equal(0.4f, reference.Opacity);
            Assert.False(reference.Visible);
            Assert.True(reference.Locked);
            Assert.Equal(json, GarmentFile.Serialize(loaded, folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ImageSizeReadsPngAndBmpHeaders()
    {
        var png = ImageSize.TryRead(Path.Combine(RepoRoot(), "docs", "images", "avatar-2d.png"));
        Assert.NotNull(png);
        Assert.Equal(781, png!.Value.Width);
        Assert.Equal(944, png.Value.Height);

        string bmp = Path.Combine(Path.GetTempPath(), $"seamstress-size-{Guid.NewGuid():N}.bmp");
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 123);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -45);
        File.WriteAllBytes(bmp, bytes);
        try
        {
            Assert.Equal((123, 45), ImageSize.TryRead(bmp));
        }
        finally
        {
            File.Delete(bmp);
        }
    }

    [Fact]
    public void ImageSizeReadsJpegGifAndWebpHeaders()
    {
        var jpeg = new byte[39];
        jpeg[0] = 0xFF; jpeg[1] = 0xD8;
        jpeg[2] = 0xFF; jpeg[3] = 0xE0;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4), 16);
        jpeg[20] = 0xFF; jpeg[21] = 0xC0;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(22), 17);
        jpeg[24] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(25), 52);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(27), 86);
        jpeg[29] = 3;

        var gif = new byte[10];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 31);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 17);

        var webp = new byte[40];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        "VP8X"u8.CopyTo(webp.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(webp.AsSpan(16), 10);
        webp[24] = 99;
        webp[27] = 49;

        Assert.Equal((86, 52), ImageSize.TryRead(new MemoryStream(jpeg)));
        Assert.Equal((31, 17), ImageSize.TryRead(new MemoryStream(gif)));
        Assert.Equal((100, 50), ImageSize.TryRead(new MemoryStream(webp)));
    }

    [Fact]
    public void AddingAndDeletingReferencesAreUndoable()
    {
        var ws = new Workspace();
        var reference = Add(ws);

        Assert.Same(reference, Assert.Single(ws.Document.References));
        Assert.Equal(reference.Id, ws.SelectedReferenceId);
        Assert.True(ws.History.CanUndo);

        ws.History.Undo();
        Assert.Empty(ws.Document.References);
        Assert.Null(ws.SelectedReferenceId);

        ws.History.Redo();
        Assert.Single(ws.Document.References);
        ws.SelectReference(reference.Id);
        ws.DeleteSelection();
        Assert.Empty(ws.Document.References);

        ws.History.Undo();
        Assert.Single(ws.Document.References);
    }

    [Fact]
    public void ReferenceValuesAreUndoable()
    {
        var ws = new Workspace();
        var reference = Add(ws);

        ws.SetReferenceValue("x", 55);
        ws.SetReferenceValue("opacity", 2);
        var changed = Assert.Single(ws.Document.References);
        Assert.Equal(55f, changed.Position.X);
        Assert.Equal(1f, changed.Opacity);
        Assert.Equal(55f, reference.Position.X); // the same instance is still in the current document

        ws.History.Undo();
        ws.History.Undo();
        var restored = Assert.Single(ws.Document.References);
        Assert.Equal(0f, restored.Position.X);
        Assert.Equal(ReferenceImage.DefaultOpacity, restored.Opacity);
    }

    [Fact]
    public void SceneShowsReferencesOnlyInThePatternView()
    {
        var ws = new Workspace();
        var reference = Add(ws, new Vector2(5, 6), 20, 10);
        var editor = new PatternEditor(ws);

        var scene = JsonDocument.Parse(editor.SceneJson(1024)).RootElement;
        var shown = Assert.Single(scene.GetProperty("references").EnumerateArray());
        Assert.Equal(reference.Id, shown.GetProperty("id").GetString());
        Assert.Equal(5f, shown.GetProperty("x").GetSingle());
        Assert.Equal(20f, shown.GetProperty("width").GetSingle());
        Assert.True(shown.GetProperty("selected").GetBoolean());

        editor.UvMode = true;
        Assert.Empty(JsonDocument.Parse(editor.SceneJson(1024)).RootElement.GetProperty("references").EnumerateArray());

        editor.UvMode = false;
        editor.ShowReferences = false;
        Assert.Empty(JsonDocument.Parse(editor.SceneJson(1024)).RootElement.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public void ReferencesCanBeSelectedMovedScaledAndRotated()
    {
        var ws = new Workspace();
        var reference = Add(ws);
        var editor = new PatternEditor(ws);
        const float tolerance = 0.5f;

        // Click selects the picture and drags it; the drag is one undo step.
        ws.ClearSelection();
        editor.PointerDown(Vector2.Zero, tolerance, false);
        Assert.Equal(reference.Id, ws.SelectedReferenceId);
        editor.PointerMove(new Vector2(10, 5), tolerance);
        editor.PointerUp(new Vector2(10, 5));
        Assert.Equal(new Vector2(10, 5), Current(ws).Position);
        ws.History.Undo();
        Assert.Equal(Vector2.Zero, Current(ws).Position);

        // Dragging a corner scales uniformly about the opposite corner.
        editor.PointerDown(new Vector2(20, 10), tolerance, false);
        editor.PointerMove(new Vector2(40, 20), tolerance);
        editor.PointerUp(new Vector2(40, 20));
        Assert.Equal(60f, Current(ws).Width);
        Assert.Equal(30f, Current(ws).Height);
        Assert.Equal(new Vector2(10, 5), Current(ws).Position);
        ws.History.Undo();
        Assert.Equal(40f, Current(ws).Width);
        Assert.Equal(20f, Current(ws).Height);
        Assert.Equal(Vector2.Zero, Current(ws).Position);

        // The handle above the top edge turns the picture.
        float pixel = tolerance / 9f;
        float handle = Current(ws).Height / 2 + 28 * pixel;
        editor.PointerDown(new Vector2(0, handle), tolerance, false);
        editor.PointerMove(new Vector2(handle, 0), tolerance);
        editor.PointerUp(new Vector2(handle, 0));
        Assert.True(MathF.Abs(Current(ws).Rotation + 90) < 1e-3f, $"rotation was {Current(ws).Rotation}");
        ws.History.Undo();
        Assert.Equal(0f, Current(ws).Rotation);
    }

    [Fact]
    public void WebpDecoderRecognisesWebpAndRejectsOtherData()
    {
        Assert.True(WebpDecoder.IsWebp("RIFF1234WEBPVP8 "u8));
        Assert.False(WebpDecoder.IsWebp("RIFF1234NOPEvp8 "u8));

        string temp = Path.Combine(Path.GetTempPath(), $"seamstress-webp-{Guid.NewGuid():N}.webp");
        File.WriteAllBytes(temp, "not a webp picture"u8.ToArray());
        try
        {
            Assert.False(WebpDecoder.TryDecode(temp, out _, out _, out _));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void PngWriterWritesRgbaImages()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"seamstress-rgba-{Guid.NewGuid():N}.png");
        var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 255, 255, 255, 255 };
        PngWriter.WriteRgba(temp, 2, 2, rgba);
        try
        {
            Assert.Equal((2, 2), ImageSize.TryRead(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void LockedReferencesStayInPlaceAndCannotBeDeleted()
    {
        var ws = new Workspace();
        var reference = Add(ws);
        var editor = new PatternEditor(ws);

        ws.SetReferenceLocked(reference.Id, true);
        Assert.True(Current(ws).Locked);
        Assert.Null(editor.HitReference(Vector2.Zero));

        ws.ClearSelection();
        editor.PointerDown(Vector2.Zero, 0.5f, false);
        Assert.Null(ws.SelectedReferenceId);
        editor.PointerMove(new Vector2(10, 10), 0.5f);
        editor.PointerUp(new Vector2(10, 10));
        Assert.Equal(Vector2.Zero, Current(ws).Position);

        // It can still be chosen from the View menu to unlock it, but not moved numerically.
        ws.SelectReference(reference.Id);
        Assert.Equal(reference.Id, ws.SelectedReferenceId);
        ws.SetReferenceValue("x", 30);
        Assert.Equal(0f, Current(ws).Position.X);
        ws.SetReferenceValue("opacity", 0.2f);
        Assert.Equal(0.2f, Current(ws).Opacity);

        ws.DeleteSelection();
        Assert.Single(ws.Document.References);
        ws.RemoveAllReferences();
        Assert.Single(ws.Document.References);

        ws.SetReferenceLocked(reference.Id, false);
        Assert.False(Current(ws).Locked);
        ws.SelectReference(reference.Id);
        ws.DeleteSelection();
        Assert.Empty(ws.Document.References);
    }

    private static ReferenceImage Current(Workspace ws) => Assert.Single(ws.Document.References);
}
