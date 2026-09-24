using System.Text.Json;

namespace AllianceWatch;

/// <summary>Loads the bundled, low-detail Natural Earth coastline geometry for the map control.</summary>
internal static class WorldMapGeometry
{
    private static readonly Lazy<PointF[][]> LandPolygons = new(LoadLandPolygons);

    public static IReadOnlyList<PointF[]> Land => LandPolygons.Value;

    private static PointF[][] LoadLandPolygons()
    {
        var mapPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ne_110m_land.geojson");
        if (!File.Exists(mapPath)) return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(mapPath));
            var polygons = new List<PointF[]>();
            foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
            {
                var geometry = feature.GetProperty("geometry");
                var type = geometry.GetProperty("type").GetString();
                var coordinates = geometry.GetProperty("coordinates");
                if (type == "Polygon") AddPolygon(coordinates, polygons);
                else if (type == "MultiPolygon")
                    foreach (var polygon in coordinates.EnumerateArray()) AddPolygon(polygon, polygons);
            }
            return polygons.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static void AddPolygon(JsonElement polygon, List<PointF[]> polygons)
    {
        // The first ring is the outer coastline. The small-scale base map does not
        // need interior holes, which keeps the tactical display quick and legible.
        if (polygon.GetArrayLength() == 0) return;
        var ring = polygon[0];
        var points = new List<PointF>(ring.GetArrayLength());
        foreach (var coordinate in ring.EnumerateArray())
        {
            if (coordinate.GetArrayLength() < 2) continue;
            points.Add(new PointF(coordinate[0].GetSingle(), coordinate[1].GetSingle()));
        }
        if (points.Count >= 3) polygons.Add(points.ToArray());
    }
}
