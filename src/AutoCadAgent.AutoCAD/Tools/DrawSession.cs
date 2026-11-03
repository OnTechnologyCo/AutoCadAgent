using System;
using System.Collections.Generic;

using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // 도면과 CLI 세션별 도형 버퍼. draw.commit에서 실제 도면에 반영한다.
    public static class DrawSession
    {
        private static readonly object _lock = new object();
        // Isolated by live document AND CLI session; never leak pending geometry between tabs.
        private sealed class State {
            public readonly List<PendingShape> Pending = new List<PendingShape>();
            public string Description = "";
            public string Id;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Autodesk.AutoCAD.DatabaseServices.Database, Dictionary<string, State>> States = new System.Runtime.CompilerServices.ConditionalWeakTable<Autodesk.AutoCAD.DatabaseServices.Database, Dictionary<string, State>>();
        internal static string ClientId = "manual";
        private static State Current {
            get {
                var db = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument?.Database;
                if (db == null) throw new InvalidOperationException("활성 도면 없음");
                var map = States.GetOrCreateValue(db);
                if (!map.TryGetValue(ClientId, out var state)) {
                    if (map.Count >= 32) {
                        var empty = new List<string>();
                        foreach (var pair in map) if (pair.Value.Pending.Count == 0) empty.Add(pair.Key);
                        foreach (var key in empty) map.Remove(key);
                    }
                    if (map.Count >= 32) throw new InvalidOperationException("32개 세션에 미반영 도형이 있습니다. 사용하지 않는 세션 버퍼를 정리하세요.");
                    map[ClientId] = state = new State();
                }
                return state;
            }
        }
        private static List<PendingShape> _pending => Current.Pending;
        private static string _description { get => Current.Description; set => Current.Description = value; }
        private static string _sessionId { get => Current.Id; set => Current.Id = value; }

        public abstract class PendingShape
        {
            public string Layer;       // 옵션 — 적용 후 도면 레이어
            public int? ColorIndex;    // 옵션 — ACI 색상

            // tx, ty 는 draw.commit의 WCS 기준점. 모든 좌표는 이 위치를 (0,0) 기준으로 평행이동.
            public abstract Entity Build(double tx, double ty);
        }

        public sealed class LineShape : PendingShape
        {
            public double X1, Y1, X2, Y2;
            public override Entity Build(double tx, double ty)
                => new Line(new Point3d(X1 + tx, Y1 + ty, 0),
                            new Point3d(X2 + tx, Y2 + ty, 0));
        }

        public sealed class CircleShape : PendingShape
        {
            public double Cx, Cy, R;
            public override Entity Build(double tx, double ty)
                => new Circle(new Point3d(Cx + tx, Cy + ty, 0), Vector3d.ZAxis, R);
        }

        public sealed class ArcShape : PendingShape
        {
            public double Cx, Cy, R, StartAngleRad, EndAngleRad;
            public override Entity Build(double tx, double ty)
                => new Arc(new Point3d(Cx + tx, Cy + ty, 0), R, StartAngleRad, EndAngleRad);
        }

        public sealed class EllipseShape : PendingShape
        {
            public double Cx, Cy, MajorX, MajorY, RadiusRatio, StartAngleRad, EndAngleRad;
            public override Entity Build(double tx, double ty)
                => new Ellipse(new Point3d(Cx + tx, Cy + ty, 0), Vector3d.ZAxis,
                               new Vector3d(MajorX, MajorY, 0),
                               RadiusRatio, StartAngleRad, EndAngleRad);
        }

        public sealed class PolylineShape : PendingShape
        {
            public List<Point2d> Points;  // 로컬 좌표 (commit 기준점 = 원점)
            public bool Closed;
            public double GlobalWidth;     // 0 이면 무시
            public override Entity Build(double tx, double ty)
            {
                var pl = new Polyline();
                for (int i = 0; i < Points.Count; i++)
                {
                    var p = Points[i];
                    pl.AddVertexAt(i, new Point2d(p.X + tx, p.Y + ty), 0, GlobalWidth, GlobalWidth);
                }
                pl.Closed = Closed;
                return pl;
            }
        }

        public sealed class PointShape : PendingShape
        {
            public double X, Y;
            public override Entity Build(double tx, double ty)
                => new DBPoint(new Point3d(X + tx, Y + ty, 0));
        }

        public sealed class TextShape : PendingShape
        {
            public double X, Y, Height, RotationRad;
            public string Content;
            public bool IsMText;  // true = MText, false = DBText
            public override Entity Build(double tx, double ty)
            {
                if (IsMText)
                {
                    var mt = new MText
                    {
                        Location = new Point3d(X + tx, Y + ty, 0),
                        Contents = Content ?? "",
                        TextHeight = Height > 0 ? Height : 2.5,
                        Rotation = RotationRad,
                    };
                    return mt;
                }
                else
                {
                    var dt = new DBText
                    {
                        Position = new Point3d(X + tx, Y + ty, 0),
                        TextString = Content ?? "",
                        Height = Height > 0 ? Height : 2.5,
                        Rotation = RotationRad,
                    };
                    return dt;
                }
            }
        }

        // 치수선은 base 위치도 commit 기준점으로 평행이동.
        public sealed class DimAlignedShape : PendingShape
        {
            public double X1, Y1, X2, Y2, BaseX, BaseY;
            public string DimText;  // null 이면 자동 측정값
            public override Entity Build(double tx, double ty)
            {
                var dim = new AlignedDimension(
                    new Point3d(X1 + tx, Y1 + ty, 0),
                    new Point3d(X2 + tx, Y2 + ty, 0),
                    new Point3d(BaseX + tx, BaseY + ty, 0),
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        public sealed class DimRotatedShape : PendingShape
        {
            public double X1, Y1, X2, Y2, BaseX, BaseY, RotationRad;
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new RotatedDimension(
                    RotationRad,
                    new Point3d(X1 + tx, Y1 + ty, 0),
                    new Point3d(X2 + tx, Y2 + ty, 0),
                    new Point3d(BaseX + tx, BaseY + ty, 0),
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        public sealed class DimRadialShape : PendingShape
        {
            public double Cx, Cy, ChordX, ChordY, LeaderLen;
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new RadialDimension(
                    new Point3d(Cx + tx, Cy + ty, 0),
                    new Point3d(ChordX + tx, ChordY + ty, 0),
                    LeaderLen, DimText, ObjectId.Null);
                return dim;
            }
        }

        public sealed class DimDiametricShape : PendingShape
        {
            public double X1, Y1, X2, Y2, LeaderLen;
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new DiametricDimension(
                    new Point3d(X1 + tx, Y1 + ty, 0),
                    new Point3d(X2 + tx, Y2 + ty, 0),
                    LeaderLen, DimText, ObjectId.Null);
                return dim;
            }
        }

        public sealed class DimAngularShape : PendingShape
        {
            public double Vx, Vy, P1x, P1y, P2x, P2y, ArcX, ArcY;
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new Point3AngularDimension(
                    new Point3d(Vx + tx, Vy + ty, 0),
                    new Point3d(P1x + tx, P1y + ty, 0),
                    new Point3d(P2x + tx, P2y + ty, 0),
                    new Point3d(ArcX + tx, ArcY + ty, 0),
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        // ───────── Phase 1 추가 누적 도형 ─────────

        public sealed class SplineShape : PendingShape
        {
            public List<Point3d> FitPoints;  // 통과해야 할 점들 (로컬 좌표 — 2D면 z=0)
            public bool Closed;
            public override Entity Build(double tx, double ty)
            {
                var pts = new Point3dCollection();
                foreach (var p in FitPoints)
                    pts.Add(new Point3d(p.X + tx, p.Y + ty, p.Z));
                // Closed=true 면 첫 점을 끝에 한 번 더 추가해 닫힘 효과
                if (Closed && FitPoints.Count >= 3)
                {
                    var p0 = FitPoints[0];
                    pts.Add(new Point3d(p0.X + tx, p0.Y + ty, p0.Z));
                }
                var sp = new Spline(pts, 3, 0.0);  // 3차, 허용오차 0
                return sp;
            }
        }

        public sealed class HatchShape : PendingShape
        {
            public List<Point2d> BoundaryPoints;  // 닫힌 폴리라인 형태
            public string PatternName = "SOLID";  // SOLID 또는 ANSI31 등
            public double PatternScale = 1.0;
            public double PatternAngle = 0.0;
            public override Entity Build(double tx, double ty)
            {
                // 경계 폴리라인부터 만들고 그 핸들로 해치 부착해야 하지만,
                // 누적 모델에서는 트랜잭션 안에서 처리해야 함 — Build 만으로 못 만들고 별도 처리 필요.
                // 여기서는 폴리라인 경계를 임시 반환하고, FlushAt 에서 실제 해치 부착 처리.
                // (실제 구현은 _hatchBoundaryHack — Build 로는 폴리라인만 반환, 해치는 별도 처리)
                var pl = new Polyline();
                for (int i = 0; i < BoundaryPoints.Count; i++)
                {
                    pl.AddVertexAt(i, new Point2d(BoundaryPoints[i].X + tx, BoundaryPoints[i].Y + ty), 0, 0, 0);
                }
                pl.Closed = true;
                return pl;
            }
        }

        public sealed class DonutShape : PendingShape
        {
            // Build outer boundary; FlushAt adds inner circle and solid hatch in the same transaction.
            public double Cx, Cy, OuterDiameter, InnerDiameter;
            public override Entity Build(double tx, double ty)
            {
                // Inner boundary and fill are committed with this outer boundary.
                return new Circle(new Point3d(Cx + tx, Cy + ty, 0), Vector3d.ZAxis, OuterDiameter / 2.0);
            }
        }

        public sealed class RayShape : PendingShape
        {
            public double Bx, By, Dx, Dy;  // 시작점 + 방향벡터
            public override Entity Build(double tx, double ty)
            {
                var ray = new Ray
                {
                    BasePoint = new Point3d(Bx + tx, By + ty, 0),
                    UnitDir = new Vector3d(Dx, Dy, 0).GetNormal(),
                };
                return ray;
            }
        }

        public sealed class XlineShape : PendingShape
        {
            public double Bx, By, Dx, Dy;
            public override Entity Build(double tx, double ty)
            {
                var xl = new Xline
                {
                    BasePoint = new Point3d(Bx + tx, By + ty, 0),
                    UnitDir = new Vector3d(Dx, Dy, 0).GetNormal(),
                };
                return xl;
            }
        }

        public sealed class RevisionCloudShape : PendingShape
        {
            // RevCloud 는 Polyline 으로 호 세그먼트가 연속된 형태. 단순화하여 원형 cloud 만 지원.
            public double Cx, Cy, Rx, Ry;  // 타원형 외접 영역
            public int BulgeCount = 16;    // 호 개수
            public override Entity Build(double tx, double ty)
            {
                var pl = new Polyline();
                for (int i = 0; i < BulgeCount; i++)
                {
                    double ang = 2 * Math.PI * i / BulgeCount;
                    double x = Rx * Math.Cos(ang);
                    double y = Ry * Math.Sin(ang);
                    // bulge=0.5 로 호 세그먼트 (1/4 호)
                    pl.AddVertexAt(i, new Point2d(Cx + tx + x, Cy + ty + y), 0.5, 0, 0);
                }
                pl.Closed = true;
                return pl;
            }
        }

        // 선형 치수 (수평/수직 분리)
        public sealed class DimLinearShape : PendingShape
        {
            public double X1, Y1, X2, Y2, BaseX, BaseY;
            public bool Horizontal;  // true=수평, false=수직
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var rotation = Horizontal ? 0.0 : Math.PI / 2;
                var dim = new RotatedDimension(
                    rotation,
                    new Point3d(X1 + tx, Y1 + ty, 0),
                    new Point3d(X2 + tx, Y2 + ty, 0),
                    new Point3d(BaseX + tx, BaseY + ty, 0),
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        // 호 길이 치수
        public sealed class DimArcLengthShape : PendingShape
        {
            public double Cx, Cy, ArcStartX, ArcStartY, ArcEndX, ArcEndY, BaseX, BaseY;
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new ArcDimension(
                    new Point3d(Cx + tx, Cy + ty, 0),
                    new Point3d(ArcStartX + tx, ArcStartY + ty, 0),
                    new Point3d(ArcEndX + tx, ArcEndY + ty, 0),
                    new Point3d(BaseX + tx, BaseY + ty, 0),
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        // ───────── Phase 2 추가 누적 도형 ─────────

        // 좌표(원점) 치수 (Ordinate Dimension)
        public sealed class DimOrdinateShape : PendingShape
        {
            public double FeatureX, FeatureY, LeaderX, LeaderY;
            public bool UsingXAxis;  // true=X 좌표 치수, false=Y 좌표 치수
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new OrdinateDimension(
                    UsingXAxis,
                    new Point3d(FeatureX + tx, FeatureY + ty, 0),
                    new Point3d(LeaderX + tx, LeaderY + ty, 0),
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        // Jogged 반지름 치수 (큰 원의 반지름을 일부만 표시)
        public sealed class DimJoggedShape : PendingShape
        {
            public double Cx, Cy, ChordX, ChordY, JogX, JogY, OverrideX, OverrideY;
            public string DimText;
            public override Entity Build(double tx, double ty)
            {
                var dim = new RadialDimensionLarge(
                    new Point3d(Cx + tx, Cy + ty, 0),
                    new Point3d(ChordX + tx, ChordY + ty, 0),
                    new Point3d(OverrideX + tx, OverrideY + ty, 0),
                    new Point3d(JogX + tx, JogY + ty, 0),
                    Math.PI / 4,  // jog 각도 45°
                    DimText, ObjectId.Null);
                return dim;
            }
        }

        // 단일 인출선 (Leader)
        public sealed class LeaderShape : PendingShape
        {
            public List<Point2d> Vertices;  // 인출선 꺾임 정점들 (최소 2개)
            public string AnnotationText;   // 끝점에 붙일 MText (선택)
            public double TextHeight = 2.5;
            public override Entity Build(double tx, double ty)
            {
                var leader = new Leader();
                foreach (var p in Vertices)
                    leader.AppendVertex(new Point3d(p.X + tx, p.Y + ty, 0));
                leader.HasArrowHead = true;
                // 주석 텍스트는 별도 MText 로 추가하지 않고 단일 Leader 만 반환.
                // (호출 도구에서 LeaderShape + TextShape 두 번 누적 권장)
                return leader;
            }
        }

        // 톨러런스(Geometric Tolerance) — 단순화: gdt 폰트 SHX 코드를 MText 로 표현
        public sealed class ToleranceShape : PendingShape
        {
            public double X, Y;
            public string Content;  // "{\\Fgdt;j}%%v∅0.05%%vA%%vB" 형태 control frame
            public override Entity Build(double tx, double ty)
            {
                // 정식 Tolerance 엔티티 API 가 ObjectARX 버전마다 달라 MText 박스로 단순화.
                // gdt 폰트가 도면에 등록돼 있으면 \\Fgdt; 코드가 정상 렌더된다.
                var mt = new MText
                {
                    Location = new Point3d(X + tx, Y + ty, 0),
                    Contents = Content ?? "",
                    TextHeight = 2.5,
                };
                return mt;
            }
        }

        // 와이프아웃(Wipeout) — 닫힌 폴리곤으로 가려짐 영역 표시
        public sealed class WipeoutShape : PendingShape
        {
            public List<Point2d> Vertices;  // 닫힌 다각형 정점
            public override Entity Build(double tx, double ty)
            {
                var pts = new Point2dCollection();
                foreach (var p in Vertices)
                    pts.Add(new Point2d(p.X + tx, p.Y + ty));
                // 마지막 점을 시작점으로 닫음
                pts.Add(pts[0]);
                var wo = new Wipeout();
                wo.SetFrom(pts, Vector3d.ZAxis);
                return wo;
            }
        }

        // 중심 마크 (X 자 형태 두 직선)
        public sealed class CenterMarkShape : PendingShape
        {
            public double Cx, Cy, Size;
            public override Entity Build(double tx, double ty)
            {
                // Build 가 단일 Entity 반환이라 + 자 두 직선 중 하나만 반환하면 부족.
                // 호출부에서 두 번 Add 해야 함 — 타입 의미상 한 도구가 두 도형을 누적하도록 도구 쪽에서 처리.
                return new Line(
                    new Point3d(Cx + tx - Size, Cy + ty, 0),
                    new Point3d(Cx + tx + Size, Cy + ty, 0));
            }
        }

        // ─────────────────── 세션 관리 ───────────────────

        public static void StartNew(string description)
        {
            lock (_lock)
            {
                _pending.Clear();
                _description = description ?? "";
                _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        public static void Add(PendingShape s)
        {
            if (s == null) return;
            lock (_lock)
            {
                if (_sessionId == null) { StartNew(""); }
                if (_pending.Count >= 10000) throw new InvalidOperationException("누적 도형 한도는 10000개입니다.");
                _pending.Add(s);
            }
        }

        public static int Count
        {
            get { lock (_lock) { return _pending.Count; } }
        }

        public static string Description
        {
            get { lock (_lock) { return _description; } }
        }

        public static string SessionId
        {
            get { lock (_lock) { return _sessionId; } }
        }

        // 일괄 그리기 — draw.commit이 WCS 기준점을 전달한다.
        // 반환값: 생성된 엔티티 핸들 hex 리스트.
        public static List<string> FlushAt(Database db, double tx, double ty,
                                            string layerOverride, int? colorOverride)
        {
            var handles = new List<string>();
            List<PendingShape> snapshot;
            lock (_lock)
            {
                snapshot = new List<PendingShape>(_pending);
            }

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var s in snapshot)
                {
                    var ent = s.Build(tx, ty);
                    if (ent == null) continue;
                    var layer = !string.IsNullOrEmpty(layerOverride) ? layerOverride : s.Layer;
                    var color = colorOverride ?? s.ColorIndex;
                    ent.SetDatabaseDefaults(db);
                    if (ent is Dimension dimension) dimension.DimensionStyle = db.Dimstyle;
                    DrawHelpers.ApplyLayerAndColor(ent, db, layer, color);
                    var outerId = DrawHelpers.AddToModelSpace(tr, db, ent);
                    handles.Add(ent.Handle.Value.ToString("X"));
                    if (s is HatchShape || s is DonutShape) {
                        ObjectId innerId = ObjectId.Null;
                        if (s is DonutShape ring && ring.InnerDiameter > 0) {
                            var inner = new Circle(new Point3d(ring.Cx + tx, ring.Cy + ty, 0),Vector3d.ZAxis,ring.InnerDiameter / 2);
                            inner.SetDatabaseDefaults(db);
                            DrawHelpers.ApplyLayerAndColor(inner,db,layer,color);
                            innerId = DrawHelpers.AddToModelSpace(tr,db,inner);
                            handles.Add(inner.Handle.Value.ToString("X"));
                        }
                        var hatch = new Hatch();
                        hatch.SetDatabaseDefaults(db);
                        DrawHelpers.ApplyLayerAndColor(hatch,db,layer,color);
                        DrawHelpers.AddToModelSpace(tr,db,hatch);
                        var hs = s as HatchShape;
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, hs?.PatternName ?? "SOLID");
                        hatch.PatternScale = hs?.PatternScale ?? 1;
                        hatch.PatternAngle = hs?.PatternAngle ?? 0;
                        hatch.Associative = true;
                        hatch.AppendLoop(HatchLoopTypes.External,new ObjectIdCollection(new[] {outerId}));
                        if (!innerId.IsNull) hatch.AppendLoop(HatchLoopTypes.Default,new ObjectIdCollection(new[] {innerId}));
                        hatch.EvaluateHatch(true);
                        handles.Add(hatch.Handle.Value.ToString("X"));
                    }
                }
                tr.Commit();
            }

            lock (_lock) { _pending.Clear(); _sessionId = null; _description = ""; }
            return handles;
        }

        public static void Clear()
        {
            lock (_lock) { _pending.Clear(); _sessionId = null; _description = ""; }
        }
    }
}


