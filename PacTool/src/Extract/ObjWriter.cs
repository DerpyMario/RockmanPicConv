using System.Globalization;
using System.Text;
using PacTool.Formats;

namespace PacTool.Extract;

/// <summary>
/// Writes Wavefront OBJ. Chosen because it is text, needs no library to produce, and every modelling
/// tool reads it - the point of exporting geometry here is that someone can open it.
///
/// The vertices are emitted exactly as the display list stores them, one entry per drawn vertex,
/// with no welding. That keeps the file a faithful record of the stream rather than an
/// interpretation of it.
/// </summary>
public static class ObjWriter
{
    /// <summary>Writes one mesh, naming its object <paramref name="name"/>.</summary>
    public static void Save(ScnMesh mesh, string name, string path, string? materialName = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        Write(writer, mesh, name, materialName, vertexBase: 0);
    }

    /// <summary>Writes several meshes into one file, each as its own object.</summary>
    public static void SaveAll(IReadOnlyList<(string Name, ScnMesh Mesh)> meshes, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));

        int vertexBase = 0;
        foreach ((string name, ScnMesh mesh) in meshes)
        {
            Write(writer, mesh, name, materialName: null, vertexBase);
            vertexBase += mesh.Vertices.Count;
        }
    }

    private static void Write(TextWriter writer, ScnMesh mesh, string name, string? materialName, int vertexBase)
    {
        writer.WriteLine($"# {name}");
        writer.WriteLine($"# {mesh.Vertices.Count} vertices, {mesh.Triangles.Count} triangles, " +
                         $"{mesh.DisplayList.Primitives.Count} GX primitive(s)");
        writer.WriteLine($"# vertex layout: {mesh.Format.Describe()}");
        writer.WriteLine($"o {Sanitize(name)}");

        foreach (ScnVertex v in mesh.Vertices)
            writer.WriteLine($"v {F(v.X)} {F(v.Y)} {F(v.Z)}");
        foreach (ScnVertex v in mesh.Vertices)
            writer.WriteLine($"vn {F(v.Nx)} {F(v.Ny)} {F(v.Nz)}");
        foreach (ScnVertex v in mesh.Vertices)
        {
            // OBJ puts the texture origin at the bottom left, GX at the top left.
            writer.WriteLine($"vt {F(v.U)} {F(1f - v.V)}");
        }

        if (materialName is not null)
            writer.WriteLine($"usemtl {Sanitize(materialName)}");

        foreach ((int a, int b, int c) in mesh.Triangles)
        {
            int ia = vertexBase + a + 1;
            int ib = vertexBase + b + 1;
            int ic = vertexBase + c + 1;
            writer.WriteLine($"f {ia}/{ia}/{ia} {ib}/{ib}/{ib} {ic}/{ic}/{ic}");
        }

        writer.WriteLine();
    }

    /// <summary>Six decimals is well past what a 16-bit fixed-point source can carry.</summary>
    private static string F(float value) =>
        float.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "0";

    /// <summary>OBJ names are whitespace-delimited, so spaces have to go.</summary>
    private static string Sanitize(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (char c in name)
            text.Append(char.IsWhiteSpace(c) ? '_' : c);

        return text.Length == 0 ? "mesh" : text.ToString();
    }
}
