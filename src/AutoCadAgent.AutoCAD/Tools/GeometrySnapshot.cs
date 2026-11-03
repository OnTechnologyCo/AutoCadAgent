using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    internal static class GeometrySnapshot
    {
        internal static double[] Point(Point3d value) => new[] { value.X, value.Y, value.Z };
        internal static object Bounds(Extents3d value) => new { min = Point(value.MinPoint), max = Point(value.MaxPoint) };
        internal static object Read(Entity entity)
        {
            var data = new Dictionary<string, object> {
                ["handle"] = entity.Handle.ToString(), ["type"] = entity.GetType().Name,
                ["layer"] = entity.Layer, ["color"] = entity.ColorIndex,
                ["coordinateSystem"] = "WCS", ["angleUnit"] = "radians", ["geometryAvailable"] = true
            };
            var warnings = new List<string>();
            try { data["bounds"] = Bounds(entity.GeometricExtents); }
            catch { data["bounds"] = null; warnings.Add("Bounds unavailable for this entity."); }
            try {
                if (entity is Line line) {
                    data["start"] = Point(line.StartPoint); data["end"] = Point(line.EndPoint); data["length"] = line.Length;
                } else if (entity is Circle circle) {
                    data["center"] = Point(circle.Center); data["radius"] = circle.Radius; data["area"] = circle.Area;
                } else if (entity is Arc arc) {
                    data["center"] = Point(arc.Center); data["radius"] = arc.Radius;
                    data["startAngle"] = arc.StartAngle; data["endAngle"] = arc.EndAngle; data["length"] = arc.Length;
                } else if (entity is Polyline polyline) {
                    data["closed"] = polyline.Closed; data["vertexCount"] = polyline.NumberOfVertices; data["length"] = polyline.Length;
                    var vertices = new List<object>();
                    for (var i = 0; i < Math.Min(polyline.NumberOfVertices, 10000); i++) vertices.Add(new { point = Point(polyline.GetPoint3dAt(i)), bulge = polyline.GetBulgeAt(i) });
                    data["vertices"] = vertices; data["truncated"] = vertices.Count != polyline.NumberOfVertices;
                    if (polyline.Closed) data["area"] = polyline.Area;
                } else if (entity is Dimension dimension) {
                    data["measurement"] = dimension.Measurement;
                    data["measurementUnit"] = entity.GetType().Name.IndexOf("Angular", StringComparison.Ordinal) >= 0 ? "radians" : "drawing_units";
                    data["textOverride"] = dimension.DimensionText; data["textPosition"] = Point(dimension.TextPosition);
                } else if (entity is Hatch hatch) {
                    data["pattern"] = hatch.PatternName; data["scale"] = hatch.PatternScale; data["angle"] = hatch.PatternAngle;
                    data["loopCount"] = hatch.NumberOfLoops; data["area"] = hatch.Area;
                } else if (entity is DBText text) {
                    data["text"] = text.TextString; data["position"] = Point(text.Position); data["height"] = text.Height; data["rotation"] = text.Rotation;
                } else if (entity is MText mtext) {
                    data["text"] = mtext.Contents; data["position"] = Point(mtext.Location); data["height"] = mtext.TextHeight; data["rotation"] = mtext.Rotation;
                } else if (entity is BlockReference block) {
                    data["name"] = block.Name; data["position"] = Point(block.Position); data["rotation"] = block.Rotation;
                } else {
                    data["geometryAvailable"] = false;
                    warnings.Add("Only identity and bounds are available; do not infer complete geometry from bounds.");
                }
            } catch (Exception ex) { warnings.Add("Some geometry fields are unavailable: " + ex.GetType().Name); data["geometryAvailable"] = false; }
            data["warnings"] = warnings;
            return data;
        }
    }
}
