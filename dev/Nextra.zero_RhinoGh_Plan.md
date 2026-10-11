# Nextra v2 — Simplification Plan

Oct 11, 2026 · @Tatsuki

## ゴールと原則

12個の機能を 6 tool に統合し、まず skill 形式で出す。LLM の賢さに頼らず、正確さは engine 側（plugin）で担保する。

- **tool は少なく**: Rhino / GH は引数で切り替え、tool を分けない（6 tool 以内）
- **token 最適化**: GUID → 短い handle（r12, g4）、返すのは delta だけ、rules は必要なときだけ load
- **差分編集**: code も shorthand も patch で送る。全文の再送はしない
- **必ず戻せる**: 全 run の前に自動 checkpoint
- **止まらない**: timeout / dialog / error でも LLM に text で返し、作業を続けられる
- **self-improve**: 良い script・preference・mistake を保存し、人の承認つきで育てる
- **優先順位**: shorthand → library script → 自由 code（shorthand で書けないものは code に fallback）

## 構成

ユーザーは Yak で plugin を install し、`.mcpb` を desktop app に追加するだけ。GH は必要なときに server が自動で開く。

&#91;embedded content: 構成 · LLM app → bridge → Rhino 内 server\]

LLM は skill で手順を知り、bridge 経由で 6 tool を呼ぶ。処理はすべて Rhino 内の localhost server が行い、LLM には要約と delta だけが返る。

## Tool set（6 tool）

元の 12 機能はすべてこの 6 つに収まる。Ask モードでは `view` と `query` だけを公開する。

| Tool | 役割 | 主な引数 | 返り値 | 元の機能 |
| --- | --- | --- | --- | --- |
| `view` | 画像で見る | `target: rhino\|gh`, `focus?: handle[] \| cell \| bbox`, `tags: bool`, `size` | 低解像度画像 + 8×8 grid + handle tag | 1, 2, 8, 9 |
| `query` | text で調べる | `scope: rhino\|gh\|plugins\|docs\|library`, `filter`, `limit` | handle 付きの要約（paginate） | 4, 7, 12 |
| `run` | code 実行 | `lang: py\|cs`, `code \| patch \| file \| lib` | delta（added / modified / deleted）+ error + checkpoint\_id | 3, 5, GH の fallback |
| `gh_edit` | canvas 編集 | `ops`（shorthand 差分）, `script: {handle, patch}` | shorthand 形式の delta + compile / runtime error | 6, 10, 11 |
| `undo` | 戻す | `checkpoint_id` or `steps` | 戻した内容 | 新規 |
| `memory` | 覚える | `save_script \| save_pref \| log_mistake \| list \| compact` | 保存結果 / index | 新規 |

`run` は `host` 引数を持たない。Rhino の Python から `import Grasshopper` で GH も触れるため、1 つの実行環境で両方を扱う。

## 主要な仕組み

精度と安定性はすべて plugin 側の仕組みで担保する。LLM には「どれを・どこに」だけ言わせる。

**Handle**

- Rhino object → `r1, r2…`、GH component → `g1, g2…`。GUID との対応は server が持つ
- session 内で固定。新しく作られたものには自動で次の番号を振る

**Grid focus（Rhino / GH 共通）**

1. `view` が全体図に 8×8 grid（A–H × 1–8）を重ねて返す
2. LLM は `focus: "C3:E5"` と cell だけを言う
3. server が cell → world / canvas 座標に変換して zoom し直す。LLM は座標を計算しない

**Capture**

- Rhino: `ViewCapture` で Width / Height を明示して offscreen 描画。画面撮影はしない
- GH: 自前の canvas renderer。bitmap に canvas transform をかけ、各 component に描画させる。window が minimize / 非表示でも撮れる
- GH の未 bake geometry: component の preview geometry の bbox で Rhino view を zoom し、tag は DisplayConduit で描く（doc を汚さない）
- viewport を動かした場合は必ず元に戻す。失敗時は window を勝手に復元せず、`query` の text で返す

**差分編集**

- server が「最後の code / shorthand」を保持し、LLM は `patch: [{find, replace}]` だけ送る
- fix 時に返すのは error + 周辺数行のみ

**GH script を file に書き出す**

- `.nextra/scripts/<handle>.py|.cs` として保存し、file → component に同期 → recompute → error を返す
- 行範囲だけ読めるので refactor の token が減る。git で管理すれば履歴も残る

**GH を止めずに開く**

- 開く前に `GH_Document.EnableSolutions = false`、中身を確認してから solve
- `GH_DocumentIO` で editor を経由せずに読み込み、足りない plugin は list で返す
- server と UI thread を分け、N 秒 timeout で「dialog / 計算で停止中」と返す
- GH editor は `LoadEditor` → `HideEditor` で、必要なときに自動で開く

**Checkpoint**

- Rhino: 各 run を `BeginUndoRecord` / `EndUndoRecord` で包む
- GH: 変更前の document を archive に serialize して保持（undo stack に依存しない）

**Code fallback**

- shorthand で書けない操作は `run` で実行。`h("g7")` で handle → object を取得
- 実行前後の graph を比較し、delta を shorthand 形式で返す
- 「code に頼った操作」を log に残し、頻出するものは shorthand に昇格

## Skill 構成と self-improve

SKILL.md は短い入口だけにし、詳細は必要なときだけ読む file に分ける。同じ docs は `query scope:docs` からも引けるようにして、Claude 以外の LLM でも使えるようにする。

```
nextra-skill/
  SKILL.md              # 入口: 6 tool の使い方、優先順位、handle と grid のルール（〜1 画面）
  shorthand.md          # GH shorthand の文法と例
  workflows.md          # 典型手順: 作る → view で確認 → 修正
  api/
    rhino-python.md     # cheat sheet（下の章）
    rhino-csharp.md
    gh-python.md
    gh-csharp.md
  profiles/             # context profile（Karamba、社内 FEA など）
```

**Memory（user ごと、local に保存）**

| File | 中身 | prompt への載せ方 |
| --- | --- | --- |
| `library/*.py` | 良い script（docstring に目的・入力・出力） | index（名前 + 1 行）だけ。本文は `query scope:library` |
| `prefs.md` | 単位、layer 規約、命名、好みの書き方 | 全文（短く保つ） |
| `mistakes.log` | 失敗の生ログ（append-only） | 載せない |
| `rules.md` | mistakes を要約した rule | 全文 |

**Compact の流れ**: mistakes が N 件を超えたら code が起動 → 安い model（Flash / Haiku）が重複をまとめて rule 案を作る → user が承認したものだけ `rules.md` に入る。生ログは消さない。

## Roadmap

最初に「見る」と「戻す」を固め、その後に「動かす」を載せる。日付は未定。

&#91;embedded content: roadmap · 5 phase, 3 gate\]

Gate を通らないまま次の phase に進まない。特に Gate 2（undo）は、外部に出す前の必須条件。

## API cheat sheet: Rhino × Python

Rhino 8 の Python 3（ScriptEditor）で RhinoCommon を直接使う前提。rhinoscriptsyntax（`rs`）は手軽だが GUID が返るだけなので、Nextra では RhinoCommon を基本にする。

**基本の型**

| やりたいこと | 書き方 |
| --- | --- |
| doc を取る | `import scriptcontext as sc; doc = sc.doc` |
| 点・ベクトル・平面 | `rg.Point3d(x,y,z)`, `rg.Vector3d`, `rg.Plane(origin, xaxis, yaxis)`（`import Rhino.Geometry as rg`） |
| 線・曲線 | `rg.Line(a,b)`, `rg.Polyline(pts)`, `rg.NurbsCurve.Create(False, 3, pts)`, `rg.Circle(plane, r)` |
| 面・立体 | `rg.Brep.CreateFromBox(bbox)`, `rg.Extrusion.Create(crv, h, True)`, `rg.Brep.CreateFromLoft(crvs, …)` |
| 変形 | `rg.Transform.Translation(v)`, `.Rotation(ang, axis, center)`, `.Scale(center, f)` → `geo.Transform(xf)` |
| 公差 | `doc.ModelAbsoluteTolerance` |

**doc の読み書き**

| やりたいこと | 書き方 |
| --- | --- |
| 追加 | `id = doc.Objects.Add(geo, attrs)` / `AddCurve`, `AddBrep`, `AddPoint` |
| 属性付きで追加 | `attrs = Rhino.DocObjects.ObjectAttributes(); attrs.LayerIndex = i; attrs.Name = "col_01"` |
| ID で探す | `obj = doc.Objects.FindId(guid)` → `obj.Geometry`, `obj.Attributes` |
| layer の全 object | `doc.Objects.FindByLayer("Columns")` |
| 種類で絞る | `s = Rhino.DocObjects.ObjectEnumeratorSettings(); s.ObjectTypeFilter = Rhino.DocObjects.ObjectType.Curve; doc.Objects.GetObjectList(s)` |
| 選択中 | `doc.Objects.GetSelectedObjects(False, False)` |
| layer 作成 | `doc.Layers.Add("Columns", System.Drawing.Color.Red)` / 検索 `doc.Layers.FindName("Columns")` |
| user text | `obj.Attributes.SetUserString("key", "val"); obj.CommitChanges()` |
| 置き換え・削除 | `doc.Objects.Replace(guid, geo)`, `doc.Objects.Delete(guid, True)` |
| 再描画 | `doc.Views.Redraw()` |

**Nextra で必須のパターン**

```python
import Rhino, scriptcontext as sc
doc = sc.doc
sn = doc.BeginUndoRecord("Nextra run 42")   # checkpoint
try:
    # ... 処理 ...
    pass
finally:
    doc.EndUndoRecord(sn)
    doc.Views.Redraw()
```

```python
# offscreen capture（minimize 中でも動く想定、要実機確認）
view = doc.Views.ActiveView
cap = Rhino.Display.ViewCapture()
cap.Width, cap.Height = 1024, 768
cap.DrawGrid = False
bmp = cap.CaptureToBitmap(view)
bmp.Save(r"C:\temp\nextra_view.png")
```

**よくある落とし穴**

- 角度は radian（`math.radians(30)`）
- Brep の boolean は失敗すると `None` / 空配列を返す。必ずチェック
- `rg.Plane.WorldXY` は値型。変更するなら copy してから
- 曲線の向き・seam で loft がねじれる → `crv.Reverse()` や seam を揃える

参考: [RhinoCommon API](https://developer.rhino3d.com/api/rhinocommon/) · [Rhino.Python guides](https://developer.rhino3d.com/guides/rhinopython/)

## API cheat sheet: Rhino × C#

API は Python と同じ RhinoCommon。違うのは型の書き方と、out 引数・null チェック・UI thread の扱い。plugin 本体（server、capture、checkpoint）はこちらで書く。

**Python との対応**

| Python | C# |
| --- | --- |
| `sc.doc` | `RhinoDoc.ActiveDoc`（command 内なら引数の `doc`） |
| `rg.Point3d(0,0,0)` | `new Point3d(0, 0, 0)`（`using Rhino.Geometry;`） |
| `doc.Objects.Add(geo, attrs)` | `Guid id = doc.Objects.Add(geo, attrs);` |
| `None` チェック | `if (breps == null \|\| breps.Length == 0) …` |
| `rs.GetObject()` | `RhinoGet.GetOneObject("Pick", false, ObjectType.Curve, out ObjRef r);` |

**plugin で必須のパターン**

```csharp
// UI thread で実行（server は別 thread で待ち受ける）
RhinoApp.InvokeOnUiThread(new Action(() => {
    var doc = RhinoDoc.ActiveDoc;
    uint sn = doc.BeginUndoRecord("Nextra run 42");
    try { /* 処理 */ }
    finally { doc.EndUndoRecord(sn); doc.Views.Redraw(); }
}));
```

```csharp
// handle 表: GUID <-> r1, r2 …
var h2g = new Dictionary<string, Guid>();
var g2h = new Dictionary<Guid, string>();
string Handle(Guid g) {
    if (!g2h.TryGetValue(g, out var h)) { h = $"r{g2h.Count + 1}"; g2h[g] = h; h2g[h] = g; }
    return h;
}
```

```csharp
// doc の変更を追跡して delta を作る
RhinoDoc.AddRhinoObject     += (s, e) => added.Add(e.ObjectId);
RhinoDoc.DeleteRhinoObject  += (s, e) => deleted.Add(e.ObjectId);
RhinoDoc.ReplaceRhinoObject += (s, e) => modified.Add(e.ObjectId);
```

```csharp
// 未 bake の geometry に tag を描く
class TagConduit : Rhino.Display.DisplayConduit {
    public List<(Point3d pt, string tag)> Tags = new();
    protected override void DrawForeground(Rhino.Display.DrawEventArgs e) {
        foreach (var t in Tags) e.Display.Draw2dText(t.tag, System.Drawing.Color.Black, t.pt, true);
    }
}
```

**よくある落とし穴**

- UI thread 以外から doc を触ると落ちる → 必ず `InvokeOnUiThread`
- event handler 内で doc を変更しない（再入で壊れる）
- `Brep.CreateBooleanUnion` などは `doc.ModelAbsoluteTolerance` を渡す

参考: [RhinoCommon API](https://developer.rhino3d.com/api/rhinocommon/) · [RhinoCommon guides](https://developer.rhino3d.com/guides/rhinocommon/)

## API cheat sheet: Grasshopper × Python

GH の Python には 2 つの顔がある。(A) canvas 上の script component の中身、(B) Rhino 側の Python から canvas を外部操作する。Nextra の `run` は (B)、script component の編集は `gh_edit` で (A)。

**(A) script component の中身**

| やりたいこと | 書き方 |
| --- | --- |
| 入力を使う | 入力名がそのまま変数（`x`, `pts`）。Item / List / Tree は入力の右クリックで設定 |
| 出力する | 出力名に代入（`a = crv`） |
| tree を作る | `from Grasshopper import DataTree; from Grasshopper.Kernel.Data import GH_Path`\<br>`t = DataTree[object](); t.Add(v, GH_Path(0, i))` |
| component を関数として呼ぶ | `import ghpythonlib.components as ghc; ghc.Voronoi(pts)` |
| 自分自身 | `ghenv.Component`（message: `ghenv.Component.Message = "ok"`） |
| warning を出す | `ghenv.Component.AddRuntimeMessage(gh.Kernel.GH_RuntimeMessageLevel.Warning, "msg")` |

**(B) Rhino 側から canvas を操作**

| やりたいこと | 書き方 |
| --- | --- |
| GH を読み込む | `gh = Rhino.RhinoApp.GetPlugInObject("Grasshopper"); gh.LoadEditor(); gh.HideEditor()` |
| file を開く | `gh.OpenDocument(path)` |
| 今の document | `from Grasshopper import Instances; doc = Instances.ActiveCanvas.Document` |
| 全 component | `for o in doc.Objects: o.NickName, o.InstanceGuid, o.Attributes.Pivot` |
| component を探す | `doc.FindObject(guid, True)` |
| plugin の component 一覧 | `for p in Instances.ComponentServer.ObjectProxies: p.Desc.Name, p.Desc.Category, p.Guid` |
| 生成して置く | `o = Instances.ComponentServer.EmitObject(p.Guid); o.CreateAttributes(); o.Attributes.Pivot = System.Drawing.PointF(x, y); doc.AddObject(o, False)` |
| wire をつなぐ | `dst.Params.Input[0].AddSource(src.Params.Output[0])` |
| 値の入った slider | `Grasshopper.Kernel.Special.GH_NumberSlider()` → `.Slider.Minimum/Maximum/Value` |
| 再計算 | `doc.NewSolution(False)` / 1 つだけ `o.ExpireSolution(True)` |
| error を集める | `o.RuntimeMessages(GH_RuntimeMessageLevel.Error)` |
| 計算を止める | `GH_Document.EnableSolutions = False` |

**Nextra で必須のパターン**

```python
# 変更前に archive で checkpoint
from Grasshopper.Kernel import GH_DocumentIO
io = GH_DocumentIO(doc)
io.SaveQuiet(r"C:\temp\nextra_ckpt_42.gh")   # 戻すときは io.Open(path)
```

**よくある落とし穴**

- canvas 操作は UI thread で。Rhino 側 script なら通常そのまま UI thread
- `AddObject` 後に `CreateAttributes()` を忘れると表示されない
- slider の値は `Slider.Value`（Decimal）。`float()` で変換
- Python 3 と IronPython 2 の script component で使える構文・library が違う
- Rhino 8 の script component の code を外部から書き換える API は要確認

参考: [Grasshopper SDK](https://developer.rhino3d.com/api/grasshopper/) · [GhPython guides](https://developer.rhino3d.com/guides/rhinopython/)

## API cheat sheet: Grasshopper × C#

`.gha`（GH 側の plugin）と C# script component の両方で使う Grasshopper SDK。canvas の描画・viewport 操作は C# でしかやりにくいので、自前 renderer はこちらで書く。

**C# script component の中身**

| やりたいこと | 書き方 |
| --- | --- |
| 入出力 | `RunScript(Curve crv, double h, ref object A)` の引数がそのまま入出力 |
| tree | `DataTree<Point3d> t = new DataTree<Point3d>(); t.Add(p, new GH_Path(0, i));` |
| message | `Component.Message = "ok";`（Rhino 8 では `this.Component` の書き方が版で異なる、要確認） |
| print | `Print("…")` → out 出力に出る |

**canvas と document（`.gha` / 外部操作）**

| やりたいこと | 書き方 |
| --- | --- |
| canvas | `GH_Canvas cv = Instances.ActiveCanvas;` |
| document | `GH_Document doc = cv.Document;` |
| viewport | `cv.Viewport.Zoom`, `cv.Viewport.MidPoint`, `cv.Refresh()` |
| component の範囲 | `obj.Attributes.Bounds`（RectangleF、canvas 座標） |
| 生成・配置 | `var o = Instances.ComponentServer.EmitObject(guid); o.CreateAttributes(); o.Attributes.Pivot = new PointF(x, y); doc.AddObject(o, false);` |
| wire | `dst.Params.Input[0].AddSource(src.Params.Output[0]);` |
| undo record | `doc.UndoUtil.RecordAddObjectEvent("Nextra add", o);`（変更前に記録） |
| 再計算 | `doc.NewSolution(false);` / `obj.ExpireSolution(true);` |
| error | `obj.RuntimeMessages(GH_RuntimeMessageLevel.Error)` |

**自前 canvas renderer（方針）**

```csharp
// 指定範囲の canvas を bitmap に描く（window 状態に依存しない想定、要実機確認）
RectangleF zone = RectangleF.Union(a.Attributes.Bounds, b.Attributes.Bounds);
zone.Inflate(40, 40);
float s = Math.Min(1024f / zone.Width, 768f / zone.Height);
using var bmp = new Bitmap(1024, 768);
using var g = Graphics.FromImage(bmp);
g.Clear(Color.White);
g.ScaleTransform(s, s);
g.TranslateTransform(-zone.X, -zone.Y);
// ここで wire → component の順に描く。
// 案1: GH 標準の Hi-Res Image Export の内部 API を zone 指定で呼ぶ
// 案2: Bounds に箱・名前・入出力名・wire を自前で描く（見た目は簡略、確実に動く）
```

まず案1を試し、minimize 中に失敗したら案2に切り替える。案2は見た目が GH と違うが、LLM が構造を読むには十分。

**よくある落とし穴**

- `AddObject` の第 2 引数（recordUndo）は自前 checkpoint を使うなら false
- solve 中の document 変更は禁止 → `doc.ScheduleSolution(1, cb)` の callback 内で変更する
- `ExpireSolution(true)` を連発すると計算が何度も走る → まとめて変更してから 1 回

参考: [Grasshopper SDK](https://developer.rhino3d.com/api/grasshopper/) · [Grasshopper component guides](https://developer.rhino3d.com/guides/grasshopper/)

## 未決事項とリスク

一番大きいリスクは capture が minimize 中に動くかどうか。Phase 1 の最初に実機で確かめる。

- [ ] `ViewCapture` と GH Hi-Res Export API が minimize / editor 非表示で動くか
- [ ] Rhino 8 の script component の code を外部から書き換えて recompute する API
- [ ] Mac 対応をするか（GH の描画まわりは Windows 前提で書いている）
- [ ] handle を session をまたいで保持するか（object の UserString に保存する案）
- [ ] skill は file で配るので中身は見える。保護が必要になったら `query scope:docs` を server 配信に移す
- [ ] Claude 以外の desktop app（ChatGPT など）の MCP 対応状況を個別に確認
- [ ] localhost server の token 認証と `127.0.0.1` 限定 bind を最初から入れる（任意 code 実行のため）
