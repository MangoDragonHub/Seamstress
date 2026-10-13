using System.IO.Compression;
using System.Text;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

/// <summary>
/// Binary FBX 7.4 writer implemented from the file format itself (no Autodesk SDK). An FBX file is a tree of named
/// nodes, each carrying a list of typed properties; this writer builds that tree and serializes it.
/// </summary>
public static class FbxExporter
{
    private const uint Version = 7400;

    // Values an FBX reader checks for consistency: a fixed file id and creation time with the footer id derived from them.
    private static readonly byte[] FileId = { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 };
    private const string CreationTime = "1970-01-01 10:00:00:000";
    private static readonly byte[] FooterId = { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
    private static readonly byte[] FooterMagic = { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };

    /// <summary>A node of the FBX document tree.</summary>
    internal sealed class Node
    {
        public string Name { get; }
        public List<object> Properties { get; } = new();
        public List<Node> Children { get; } = new();

        public Node(string name, params object[] properties)
        {
            Name = name;
            Properties.AddRange(properties);
        }

        public Node Add(string name, params object[] properties)
        {
            var child = new Node(name, properties);
            Children.Add(child);
            return child;
        }

        /// <summary>Adds a "P" entry to a Properties70 node.</summary>
        public Node P(string name, string type, string label, string flags, params object[] values)
        {
            var all = new object[4 + values.Length];
            all[0] = name; all[1] = type; all[2] = label; all[3] = flags;
            values.CopyTo(all, 4);
            return Add("P", all);
        }
    }

    /// <summary>Marks a byte array as a raw ('R') property rather than an array of bytes.</summary>
    internal sealed record Raw(byte[] Data);

    public static void Export(ExportScene scene, string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var root = BuildDocument(scene, folder);
        using var file = File.Create(path);
        Write(root, file);
    }

    internal static List<Node> BuildDocument(ExportScene scene, string? textureFolder)
    {
        long nextId = 1_000_000;
        long NewId() => nextId++;
        const double cm = 100.0; // FBX's default unit is the centimetre

        var top = new List<Node>();
        var now = DateTime.Now;

        var header = new Node("FBXHeaderExtension");
        header.Add("FBXHeaderVersion", 1003);
        header.Add("FBXVersion", (int)Version);
        header.Add("EncryptionType", 0);
        var stamp = header.Add("CreationTimeStamp");
        stamp.Add("Version", 1000);
        stamp.Add("Year", now.Year);
        stamp.Add("Month", now.Month);
        stamp.Add("Day", now.Day);
        stamp.Add("Hour", now.Hour);
        stamp.Add("Minute", now.Minute);
        stamp.Add("Second", now.Second);
        stamp.Add("Millisecond", now.Millisecond);
        header.Add("Creator", "Seamstress FBX Exporter");
        var sceneInfo = header.Add("SceneInfo", "GlobalInfo\0\u0001SceneInfo", "UserData");
        sceneInfo.Add("Type", "UserData");
        sceneInfo.Add("Version", 100);
        var meta = sceneInfo.Add("MetaData");
        meta.Add("Version", 100);
        meta.Add("Title", "");
        meta.Add("Subject", "");
        meta.Add("Author", "");
        meta.Add("Keywords", "");
        meta.Add("Revision", "");
        meta.Add("Comment", "");
        var infoProps = sceneInfo.Add("Properties70");
        infoProps.P("DocumentUrl", "KString", "Url", "", "");
        infoProps.P("SrcDocumentUrl", "KString", "Url", "", "");
        infoProps.P("Original", "Compound", "", "");
        infoProps.P("Original|ApplicationVendor", "KString", "", "", "Seamstress");
        infoProps.P("Original|ApplicationName", "KString", "", "", "Seamstress");
        infoProps.P("Original|ApplicationVersion", "KString", "", "", "1.0");
        infoProps.P("LastSaved", "Compound", "", "");
        infoProps.P("LastSaved|ApplicationVendor", "KString", "", "", "Seamstress");
        infoProps.P("LastSaved|ApplicationName", "KString", "", "", "Seamstress");
        infoProps.P("LastSaved|ApplicationVersion", "KString", "", "", "1.0");
        top.Add(header);

        top.Add(new Node("FileId", new Raw(FileId)));
        top.Add(new Node("CreationTime", CreationTime));
        top.Add(new Node("Creator", "Seamstress FBX Exporter"));

        var settings = new Node("GlobalSettings");
        settings.Add("Version", 1000);
        var gp = settings.Add("Properties70");
        gp.P("UpAxis", "int", "Integer", "", 1);
        gp.P("UpAxisSign", "int", "Integer", "", 1);
        gp.P("FrontAxis", "int", "Integer", "", 2);
        gp.P("FrontAxisSign", "int", "Integer", "", 1);
        gp.P("CoordAxis", "int", "Integer", "", 0);
        gp.P("CoordAxisSign", "int", "Integer", "", 1);
        gp.P("OriginalUpAxis", "int", "Integer", "", -1);
        gp.P("OriginalUpAxisSign", "int", "Integer", "", 1);
        gp.P("UnitScaleFactor", "double", "Number", "", 1.0);
        gp.P("OriginalUnitScaleFactor", "double", "Number", "", 1.0);
        gp.P("AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0);
        gp.P("DefaultCamera", "KString", "", "", "Producer Perspective");
        gp.P("TimeMode", "enum", "", "", 11);
        gp.P("TimeSpanStart", "KTime", "Time", "", 0L);
        gp.P("TimeSpanStop", "KTime", "Time", "", 46186158000L);
        gp.P("CustomFrameRate", "double", "Number", "", -1.0);
        top.Add(settings);

        var documents = new Node("Documents");
        documents.Add("Count", 1);
        var document = documents.Add("Document", NewId(), "Scene", "Scene");
        var dp = document.Add("Properties70");
        dp.P("SourceObject", "object", "", "");
        dp.P("ActiveAnimStackName", "KString", "", "", "");
        document.Add("RootNode", 0L);
        top.Add(documents);

        top.Add(new Node("References"));

        var materials = scene.Materials().ToList();
        var materialNames = ObjExporter.MaterialNames(scene);
        var textured = materials.Where(m => !string.IsNullOrEmpty(m.Texture) && File.Exists(m.Texture)).ToList();

        var definitions = new Node("Definitions");
        definitions.Add("Version", 100);
        definitions.Add("Count", 1 + scene.Meshes.Count * 2 + materials.Count + textured.Count * 2);
        definitions.Add("ObjectType", "GlobalSettings").Add("Count", 1);
        void Define(string type, int count, string template)
        {
            if (count == 0)
                return;
            var objectType = definitions.Add("ObjectType", type);
            objectType.Add("Count", count);
            objectType.Add("PropertyTemplate", template).Add("Properties70");
        }
        Define("Model", scene.Meshes.Count, "FbxNode");
        Define("Geometry", scene.Meshes.Count, "FbxMesh");
        Define("Material", materials.Count, "FbxSurfacePhong");
        Define("Texture", textured.Count, "FbxFileTexture");
        Define("Video", textured.Count, "FbxVideo");
        top.Add(definitions);

        var objects = new Node("Objects");
        var connections = new Node("Connections");

        var materialIds = new Dictionary<FabricMaterial, long>();
        foreach (var material in materials)
        {
            long id = NewId();
            materialIds[material] = id;
            var (r, g, b) = ExportScene.ParseColor(material.Color);
            var node = objects.Add("Material", id, materialNames[material] + "\0\u0001Material", "");
            node.Add("Version", 102);
            node.Add("ShadingModel", "phong");
            node.Add("MultiLayer", 0);
            var mp = node.Add("Properties70");
            double specular = (1 - material.Roughness) * 0.5;
            mp.P("DiffuseColor", "Color", "", "A", (double)r, (double)g, (double)b);
            mp.P("DiffuseFactor", "Number", "", "A", 1.0);
            mp.P("AmbientColor", "Color", "", "A", 0.0, 0.0, 0.0);
            mp.P("SpecularColor", "Color", "", "A", specular, specular, specular);
            mp.P("SpecularFactor", "Number", "", "A", 1.0);
            mp.P("Shininess", "Number", "", "A", (double)((1 - material.Roughness) * 100));
            mp.P("ShininessExponent", "Number", "", "A", (double)((1 - material.Roughness) * 100));
            mp.P("TransparencyFactor", "Number", "", "A", (double)(1 - material.Opacity));
            mp.P("Opacity", "double", "Number", "", (double)material.Opacity);

            if (textured.Contains(material))
            {
                string source = material.Texture!;
                string relative = textureFolder != null ? TextureFiles.CopyNextTo(source, textureFolder) ?? Path.GetFileName(source) : Path.GetFileName(source);
                string absolute = textureFolder != null ? Path.Combine(textureFolder, relative) : source;
                string textureName = materialNames[material] + "_diffuse";
                long videoId = NewId(), textureId = NewId();

                var video = objects.Add("Video", videoId, textureName + "\0\u0001Video", "Clip");
                video.Add("Type", "Clip");
                video.Add("Properties70").P("Path", "KString", "XRefUrl", "", absolute);
                video.Add("UseMipMap", 0);
                video.Add("Filename", absolute);
                video.Add("RelativeFilename", relative);

                var texture = objects.Add("Texture", textureId, textureName + "\0\u0001Texture", "");
                texture.Add("Type", "TextureVideoClip");
                texture.Add("Version", 202);
                texture.Add("TextureName", textureName + "\0\u0001Texture");
                var tp = texture.Add("Properties70");
                tp.P("UVSet", "KString", "", "", "UVMap");
                tp.P("UseMaterial", "bool", "", "", 1);
                tp.P("Scaling", "Vector", "", "A", (double)material.Repeat, (double)material.Repeat, 1.0);
                texture.Add("Media", textureName + "\0\u0001Video");
                texture.Add("FileName", absolute);
                texture.Add("RelativeFilename", relative);
                texture.Add("ModelUVTranslation", 0.0, 0.0);
                texture.Add("ModelUVScaling", 1.0, 1.0);
                texture.Add("Texture_Alpha_Source", "None");
                texture.Add("Cropping", 0, 0, 0, 0);

                connections.Add("C", "OO", videoId, textureId);
                connections.Add("C", "OP", textureId, id, "DiffuseColor");
            }
        }

        foreach (var mesh in scene.Meshes)
        {
            long geometryId = NewId(), modelId = NewId();
            string name = ExportScene.SafeName(mesh.Name);
            var meshMaterials = mesh.Parts.Select(p => p.Material).Distinct().ToList();

            var vertices = new double[mesh.Positions.Length * 3];
            for (int i = 0; i < mesh.Positions.Length; i++)
            {
                vertices[3 * i] = mesh.Positions[i].X * cm;
                vertices[3 * i + 1] = mesh.Positions[i].Y * cm;
                vertices[3 * i + 2] = mesh.Positions[i].Z * cm;
            }
            int corners = mesh.Parts.Sum(p => p.FacePositionIndices.Length);
            var polygonIndex = new int[corners];
            var normals = new double[corners * 3];
            var uvIndex = new int[corners];
            var faceMaterials = new int[mesh.FaceCount];
            int c = 0, face = 0;
            foreach (var part in mesh.Parts)
            {
                int slot = meshMaterials.IndexOf(part.Material);
                int k = 0;
                foreach (int size in part.FaceSizes)
                {
                    faceMaterials[face++] = slot;
                    for (int corner = 0; corner < size; corner++, k++, c++)
                    {
                        int p = part.FacePositionIndices[k];
                        // The last corner of each polygon is stored bit-inverted to mark where the polygon ends.
                        polygonIndex[c] = corner == size - 1 ? ~p : p;
                        normals[3 * c] = mesh.Normals[p].X;
                        normals[3 * c + 1] = mesh.Normals[p].Y;
                        normals[3 * c + 2] = mesh.Normals[p].Z;
                        uvIndex[c] = part.FaceUvIndices[k];
                    }
                }
            }
            var uv = new double[mesh.Uvs.Length * 2];
            for (int i = 0; i < mesh.Uvs.Length; i++)
            {
                uv[2 * i] = mesh.Uvs[i].X;
                uv[2 * i + 1] = mesh.Uvs[i].Y;
            }

            var geometry = objects.Add("Geometry", geometryId, name + "\0\u0001Geometry", "Mesh");
            geometry.Add("Properties70");
            geometry.Add("GeometryVersion", 124);
            geometry.Add("Vertices", vertices);
            geometry.Add("PolygonVertexIndex", polygonIndex);

            var normalLayer = geometry.Add("LayerElementNormal", 0);
            normalLayer.Add("Version", 101);
            normalLayer.Add("Name", "");
            normalLayer.Add("MappingInformationType", "ByPolygonVertex");
            normalLayer.Add("ReferenceInformationType", "Direct");
            normalLayer.Add("Normals", normals);

            var uvLayer = geometry.Add("LayerElementUV", 0);
            uvLayer.Add("Version", 101);
            uvLayer.Add("Name", "UVMap");
            uvLayer.Add("MappingInformationType", "ByPolygonVertex");
            uvLayer.Add("ReferenceInformationType", "IndexToDirect");
            uvLayer.Add("UV", uv);
            uvLayer.Add("UVIndex", uvIndex);

            var materialLayer = geometry.Add("LayerElementMaterial", 0);
            materialLayer.Add("Version", 101);
            materialLayer.Add("Name", "");
            materialLayer.Add("MappingInformationType", "ByPolygon");
            materialLayer.Add("ReferenceInformationType", "IndexToDirect");
            materialLayer.Add("Materials", faceMaterials);

            var layer = geometry.Add("Layer", 0);
            layer.Add("Version", 100);
            foreach (string type in new[] { "LayerElementNormal", "LayerElementUV", "LayerElementMaterial" })
            {
                var element = layer.Add("LayerElement");
                element.Add("Type", type);
                element.Add("TypedIndex", 0);
            }

            var model = objects.Add("Model", modelId, name + "\0\u0001Model", "Mesh");
            model.Add("Version", 232);
            var props = model.Add("Properties70");
            props.P("InheritType", "enum", "", "", 1);
            props.P("DefaultAttributeIndex", "int", "Integer", "", 0);
            props.P("Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0);
            props.P("Lcl Rotation", "Lcl Rotation", "", "A", 0.0, 0.0, 0.0);
            props.P("Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0);
            model.Add("MultiLayer", 0);
            model.Add("MultiTake", 0);
            model.Add("Shading", true);
            model.Add("Culling", "CullingOff");

            connections.Add("C", "OO", modelId, 0L);
            connections.Add("C", "OO", geometryId, modelId);
            foreach (var material in meshMaterials)
                connections.Add("C", "OO", materialIds[material], modelId);
        }

        top.Add(objects);
        top.Add(connections);
        var takes = new Node("Takes");
        takes.Add("Current", "");
        top.Add(takes);
        return top;
    }

    internal static void Write(List<Node> nodes, Stream output)
    {
        var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
        w.Write((byte)0x00);
        w.Write((byte)0x1A);
        w.Write((byte)0x00);
        w.Write(Version);
        foreach (var node in nodes)
            WriteNode(w, node);
        w.Write(new byte[13]); // null record ends the top-level node list

        w.Write(FooterId);
        w.Write(new byte[4]);
        long position = output.Position;
        int padding = (int)(((position + 15) & ~15L) - position);
        if (padding == 0)
            padding = 16;
        w.Write(new byte[padding]);
        w.Write(Version);
        w.Write(new byte[120]);
        w.Write(FooterMagic);
        w.Flush();
    }

    private static void WriteNode(BinaryWriter w, Node node)
    {
        var stream = w.BaseStream;
        long start = stream.Position;
        w.Write(0u); // end offset, patched below
        w.Write((uint)node.Properties.Count);
        w.Write(0u); // property list length, patched below
        var name = Encoding.ASCII.GetBytes(node.Name);
        w.Write((byte)name.Length);
        w.Write(name);

        long propertiesStart = stream.Position;
        foreach (var property in node.Properties)
            WriteProperty(w, property);
        long propertiesEnd = stream.Position;

        foreach (var child in node.Children)
            WriteNode(w, child);
        // Nodes with children, and empty nodes, are closed by a null record.
        if (node.Children.Count > 0 || node.Properties.Count == 0)
            w.Write(new byte[13]);

        long end = stream.Position;
        stream.Position = start;
        w.Write((uint)end);
        stream.Position = start + 8;
        w.Write((uint)(propertiesEnd - propertiesStart));
        stream.Position = end;
    }

    private static void WriteProperty(BinaryWriter w, object value)
    {
        switch (value)
        {
            case bool b: w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
            case short s: w.Write((byte)'Y'); w.Write(s); break;
            case int i: w.Write((byte)'I'); w.Write(i); break;
            case long l: w.Write((byte)'L'); w.Write(l); break;
            case float f: w.Write((byte)'F'); w.Write(f); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case string s:
                {
                    var bytes = Encoding.UTF8.GetBytes(s);
                    w.Write((byte)'S');
                    w.Write((uint)bytes.Length);
                    w.Write(bytes);
                    break;
                }
            case Raw raw:
                w.Write((byte)'R');
                w.Write((uint)raw.Data.Length);
                w.Write(raw.Data);
                break;
            case double[] a: WriteArray(w, 'd', a.Length, System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray()); break;
            case float[] a: WriteArray(w, 'f', a.Length, System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray()); break;
            case int[] a: WriteArray(w, 'i', a.Length, System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray()); break;
            case long[] a: WriteArray(w, 'l', a.Length, System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray()); break;
            default: throw new NotSupportedException($"FBX property type {value.GetType().Name} is not supported.");
        }
    }

    private static void WriteArray(BinaryWriter w, char type, int count, byte[] data)
    {
        w.Write((byte)type);
        w.Write((uint)count);
        if (data.Length >= 512)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(data);
            w.Write(1u); // zlib-compressed
            w.Write((uint)compressed.Length);
            w.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
        }
        else
        {
            w.Write(0u);
            w.Write((uint)data.Length);
            w.Write(data);
        }
    }
}
