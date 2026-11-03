using System;
using System.Collections.Generic;
using System.Globalization;

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoCadAgent.Cad.Tools
{
    // 범용 CAD 인자 처리 및 데이터베이스 작업 헬퍼.
    // staged 도형은 draw.commit의 WCS 기준점으로 생성한다.
    public static class DrawHelpers
    {
        // 공통 — 인자 파싱
        public static bool TryGetDouble(IDictionary<string, object> args, string key, out double v)
        {
            v = 0;
            if (args == null || !args.ContainsKey(key) || args[key] == null) return false;
            var raw = args[key];
            if (raw is double d) { v = d; return true; }
            if (raw is decimal dec) { v = (double)dec; return true; }
            if (raw is int i) { v = i; return true; }
            if (raw is long l) { v = l; return true; }
            if (raw is float f) { v = f; return true; }
            return double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
        }

        public static bool TryGetInt(IDictionary<string, object> args, string key, out int v)
        {
            v = 0;
            if (!TryGetDouble(args, key, out var d)) return false;
            v = (int)Math.Round(d);
            return true;
        }

        public static string GetString(IDictionary<string, object> args, string key, string fallback = null)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null) return fallback;
            return args[key].ToString();
        }

        public static bool GetBool(IDictionary<string, object> args, string key, bool fallback = false)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null) return fallback;
            var raw = args[key];
            if (raw is bool b) return b;
            var s = raw.ToString();
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        // [[x,y],[x,y],...] 배열을 Point2dCollection 으로 변환
        public static bool TryGetPointArray(IDictionary<string, object> args, string key, out Point2dCollection pts)
        {
            pts = new Point2dCollection();
            if (args == null || !args.ContainsKey(key) || args[key] == null) return false;
            var raw = args[key];
            if (!(raw is object[] arr)) return false;
            foreach (var item in arr)
            {
                if (!(item is object[] pair) || pair.Length < 2) return false;
                if (!TryParseDouble(pair[0], out var x)) return false;
                if (!TryParseDouble(pair[1], out var y)) return false;
                pts.Add(new Point2d(x, y));
            }
            return pts.Count >= 2;
        }

        // [hex,hex,...] 핸들 배열 추출
        public static List<string> GetHandleArray(IDictionary<string, object> args, string key)
        {
            var result = new List<string>();
            if (args == null || !args.ContainsKey(key) || args[key] == null) return result;
            var raw = args[key];
            if (raw is object[] arr)
            {
                foreach (var item in arr)
                {
                    if (item == null) continue;
                    var s = item.ToString().Trim();
                    if (s.Length > 0) result.Add(s);
                }
            }
            else
            {
                // 단일 핸들 문자열도 허용
                var s = raw.ToString().Trim();
                if (s.Length > 0) result.Add(s);
            }
            return result;
        }

        private static bool TryParseDouble(object o, out double v)
        {
            v = 0;
            if (o is double d) { v = d; return true; }
            if (o is decimal dec) { v = (double)dec; return true; }
            if (o is int i) { v = i; return true; }
            if (o is long l) { v = l; return true; }
            if (o is float f) { v = f; return true; }
            return double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out v);
        }

        // 활성 도면. 없으면 ToolResult.Fail 용 헬퍼와 함께 사용.
        public static Document GetActiveDoc(out string error)
        {
            error = null;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { error = "활성 도면이 없습니다."; return null; }
            return doc;
        }

        // 핸들 hex → ObjectId. 실패 시 ObjectId.Null.
        public static ObjectId HandleToObjectId(Database db, string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return ObjectId.Null;
            try
            {
                var h = Convert.ToInt64(hex, 16);
                return db.GetObjectId(false, new Handle(h), 0);
            }
            catch { return ObjectId.Null; }
        }

        // 레이어 / 색상 인자가 있으면 엔티티에 적용.
        // 레이어가 도면에 없으면 자동 생성 (검은색).
        public static void ApplyLayerAndColor(Entity e, Database db, string layer, int? colorIndex)
        {
            if (!string.IsNullOrWhiteSpace(layer))
            {
                EnsureLayer(db, layer);
                e.Layer = layer;
            }
            if (colorIndex.HasValue)
            {
                e.ColorIndex = Math.Max(0, Math.Min(256, colorIndex.Value));
            }
        }

        public static void EnsureLayer(Database db, string layerName)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (lt.Has(layerName)) { tr.Commit(); return; }
                lt.UpgradeOpen();
                var rec = new LayerTableRecord { Name = layerName };
                lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                tr.Commit();
            }
        }

        // 인-트랜잭션 ModelSpace 추가 헬퍼.
        public static ObjectId AddToModelSpace(Transaction tr, Database db, Entity e)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var id = ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            return id;
        }

        // 도(degree) → 라디안
        public static double Deg2Rad(double deg) => deg * Math.PI / 180.0;

        // 핸들 배열 → ObjectId 배열 (트랜잭션 밖에서 변환).
        // invalidCount가 있으면 호출자는 전체 변경을 거절한다. 중복 핸들도 거절한다.
        public static List<ObjectId> HandlesToObjectIds(Database db, List<string> handles, out int invalidCount)
        {
            var ids = new List<ObjectId>();
            var seen = new HashSet<ObjectId>();
            invalidCount = 0;
            foreach (var h in handles)
            {
                var id = HandleToObjectId(db, h);
                if (id.IsNull || !id.IsValid || id.IsErased || !seen.Add(id)) { invalidCount++; continue; }
                using (var transaction = db.TransactionManager.StartOpenCloseTransaction()) {
                    try { if (transaction.GetObject(id, OpenMode.ForRead) is Entity) ids.Add(id); else invalidCount++; }
                    catch { invalidCount++; }
                }
            }
            return ids;
        }
    }
}


