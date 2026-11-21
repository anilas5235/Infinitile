using UnityEngine;
using UnityEngine.UIElements;

namespace UI{
    /// <summary>
    ///     A voxel drawn as a small cube seen from above: the top texture and two shaded side faces. A plant or
    ///     other non-cube voxel is shown flat with one texture.
    /// </summary>
    public class BlockIcon : VisualElement{
        private static readonly Color32 TopTint = new(255, 255, 255, 255);
        private static readonly Color32 LeftTint = new(206, 206, 206, 255);
        private static readonly Color32 RightTint = new(160, 160, 160, 255);

        private Texture2D _top, _side, _overlay;
        private bool _flat;
        private Color _tint = Color.white, _sideTint = Color.white, _overlayTint = Color.white;

        public BlockIcon(){
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        /// <summary>
        ///     Shows a cube with these textures, or one flat texture; null textures draw nothing. tint multiplies
        ///     every face (a liquid's colour on its grey pattern); white when null. sideTint, when set, multiplies
        ///     the side faces instead, and overlay is drawn over both side faces multiplied by overlayTint (a
        ///     biome-tinted grass side: the dirt untinted, the grass band through its overlay, DEC-122).
        /// </summary>
        public void Set(Texture2D top, Texture2D side, bool flat, Color? tint = null, Color? sideTint = null,
            Texture2D overlay = null, Color? overlayTint = null){
            _top = top;
            _side = side ? side : top;
            _flat = flat;
            _tint = tint ?? Color.white;
            _sideTint = sideTint ?? _tint;
            _overlay = overlay;
            _overlayTint = overlayTint ?? _tint;
            MarkDirtyRepaint();
        }

        /// <summary>Height of an isometric cube over its width: the top diamond and a vertical edge, each width / √3.</summary>
        private const float CubeHeightPerWidth = 2f / 1.7320508f;

        private void Draw(MeshGenerationContext context){
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f || !_top) return;

            if (_flat){
                float size = Mathf.Min(rect.width, rect.height);
                float fx = rect.x + (rect.width - size) * 0.5f, fy = rect.y + (rect.height - size) * 0.5f;
                // half a pixel in from every edge: the tiles repeat, so a sample on the edge would pick up the
                // opposite edge (a plant's opaque stem base drew a dashed line along the icon's top)
                Quad(context, _top, Tinted(TopTint, _tint), new Vector2(fx, fy), new Vector2(fx + size, fy),
                    new Vector2(fx + size, fy + size), new Vector2(fx, fy + size), 0.5f / size);
                return;
            }

            // An isometric cube, as wide as fits: a diamond on top over two side faces, every edge equally long on
            // screen, so the cube is taller than wide.
            float width = Mathf.Min(rect.width, rect.height / CubeHeightPerWidth);
            float edge = width / 1.7320508f; // the diamond's height and the vertical edges
            float x0 = rect.x + (rect.width - width) * 0.5f;
            float y0 = rect.y + (rect.height - width * CubeHeightPerWidth) * 0.5f;
            float cx = x0 + width * 0.5f, right = x0 + width;
            float side = y0 + edge * 0.5f, mid = y0 + edge;
            Vector2 top = new(cx, y0), leftTop = new(x0, side), rightTop = new(right, side);
            Vector2 centre = new(cx, mid), leftBottom = new(x0, side + edge), rightBottom = new(right, side + edge);
            Vector2 low = new(cx, mid + edge);
            Quad(context, _top, Tinted(TopTint, _tint), leftTop, top, rightTop, centre);
            Quad(context, _side, Tinted(LeftTint, _sideTint), leftTop, centre, low, leftBottom);
            Quad(context, _side, Tinted(RightTint, _sideTint), centre, rightTop, rightBottom, low);
            if (!_overlay) return;
            Quad(context, _overlay, Tinted(LeftTint, _overlayTint), leftTop, centre, low, leftBottom);
            Quad(context, _overlay, Tinted(RightTint, _overlayTint), centre, rightTop, rightBottom, low);
        }

        private static Color32 Tinted(Color32 shade, Color tint){
            return (Color)shade * tint;
        }

        /// <summary>One textured quad; corners clockwise from the texture's top left; inset pulls the texture in.</summary>
        private static void Quad(MeshGenerationContext context, Texture2D texture, Color32 tint, Vector2 topLeft,
            Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft, float inset = 0f){
            MeshWriteData mesh = context.Allocate(4, 6, texture);
            Rect uv = mesh.uvRegion; // the texture may sit in an atlas
            if (inset > 0f){
                // inset is a share of the quad: the same share of the texture's rect
                float du = uv.width * inset, dv = uv.height * inset;
                uv = Rect.MinMaxRect(uv.xMin + du, uv.yMin + dv, uv.xMax - du, uv.yMax - dv);
            }

            mesh.SetNextVertex(MakeVertex(topLeft, uv.xMin, uv.yMax, tint));
            mesh.SetNextVertex(MakeVertex(topRight, uv.xMax, uv.yMax, tint));
            mesh.SetNextVertex(MakeVertex(bottomRight, uv.xMax, uv.yMin, tint));
            mesh.SetNextVertex(MakeVertex(bottomLeft, uv.xMin, uv.yMin, tint));
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(1);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(3);
        }

        private static Vertex MakeVertex(Vector2 position, float u, float v, Color32 tint){
            return new Vertex{
                position = new Vector3(position.x, position.y, Vertex.nearZ),
                uv = new Vector2(u, v),
                tint = tint
            };
        }
    }

    /// <summary>The small vector icons of the menus.</summary>
    public enum GlyphKind{
        Play,
        Gear,
        Exit,
        Back,
        Check,
        Warning,
        Close,
        Search
    }

    /// <summary>A vector icon drawn in a 24 × 24 box scaled to the element, centred.</summary>
    public class HudGlyph : VisualElement{
        private readonly Color _color;
        private readonly GlyphKind _kind;

        public HudGlyph(GlyphKind kind, Color color){
            _kind = kind;
            _color = color;
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = style.top = style.right = style.bottom = 0;
            generateVisualContent += Draw;
        }

        private void Draw(MeshGenerationContext context){
            Rect rect = contentRect;
            float scale = Mathf.Min(rect.width, rect.height) / 24f * 0.62f;
            if (scale <= 0f) return;
            Vector2 origin = rect.center - new Vector2(12f, 12f) * scale;
            Painter2D painter = context.painter2D;
            painter.fillColor = _color;
            painter.strokeColor = _color;
            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            painter.lineWidth = 2.6f * scale;

            Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;

            switch (_kind){
                case GlyphKind.Play:
                    Polygon(painter, P(7f, 4f), P(20f, 12f), P(7f, 20f));
                    painter.Fill();
                    break;
                case GlyphKind.Gear:
                    painter.BeginPath();
                    const int teeth = 8;
                    for (int i = 0; i < teeth * 4; i++){
                        // tooth top, tooth top, root, root: four points per tooth around the wheel
                        float angle = (i - 0.5f) * Mathf.PI * 2f / (teeth * 4);
                        float radius = i % 4 < 2 ? 11f : 8.2f;
                        Vector2 point = P(12f + Mathf.Cos(angle) * radius, 12f + Mathf.Sin(angle) * radius);
                        if (i == 0) painter.MoveTo(point);
                        else painter.LineTo(point);
                    }

                    painter.ClosePath();
                    painter.MoveTo(P(15.5f, 12f));
                    painter.Arc(P(12f, 12f), 3.5f * scale, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
                    painter.ClosePath();
                    painter.Fill(FillRule.OddEven);
                    break;
                case GlyphKind.Exit:
                    painter.BeginPath();
                    painter.MoveTo(P(13f, 4f));
                    painter.LineTo(P(4f, 4f));
                    painter.LineTo(P(4f, 20f));
                    painter.LineTo(P(13f, 20f));
                    painter.Stroke();
                    painter.BeginPath();
                    painter.MoveTo(P(9f, 12f));
                    painter.LineTo(P(21f, 12f));
                    painter.MoveTo(P(16.5f, 7.5f));
                    painter.LineTo(P(21f, 12f));
                    painter.LineTo(P(16.5f, 16.5f));
                    painter.Stroke();
                    break;
                case GlyphKind.Back:
                    Polygon(painter, P(3f, 12f), P(12f, 3.5f), P(12f, 8.5f), P(21f, 8.5f), P(21f, 15.5f),
                        P(12f, 15.5f), P(12f, 20.5f));
                    painter.Fill();
                    break;
                case GlyphKind.Check:
                    painter.lineWidth = 3.4f * scale;
                    painter.BeginPath();
                    painter.MoveTo(P(4.5f, 12.5f));
                    painter.LineTo(P(10f, 18f));
                    painter.LineTo(P(19.5f, 6.5f));
                    painter.Stroke();
                    break;
                case GlyphKind.Warning:
                    painter.BeginPath();
                    painter.MoveTo(P(12f, 3f));
                    painter.LineTo(P(22f, 20.5f));
                    painter.LineTo(P(2f, 20.5f));
                    painter.ClosePath();
                    painter.Stroke();
                    painter.BeginPath();
                    painter.MoveTo(P(12f, 9.5f));
                    painter.LineTo(P(12f, 14f));
                    painter.MoveTo(P(12f, 17.4f));
                    painter.LineTo(P(12f, 17.5f));
                    painter.Stroke();
                    break;
                case GlyphKind.Close:
                    painter.lineWidth = 3.4f * scale;
                    painter.BeginPath();
                    painter.MoveTo(P(5f, 5f));
                    painter.LineTo(P(19f, 19f));
                    painter.MoveTo(P(19f, 5f));
                    painter.LineTo(P(5f, 19f));
                    painter.Stroke();
                    break;
                case GlyphKind.Search:
                    painter.BeginPath();
                    painter.Arc(P(10f, 10f), 6.5f * scale, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
                    painter.Stroke();
                    painter.lineWidth = 3.4f * scale;
                    painter.BeginPath();
                    painter.MoveTo(P(15f, 15f));
                    painter.LineTo(P(20.5f, 20.5f));
                    painter.Stroke();
                    break;
            }
        }

        private static void Polygon(Painter2D painter, params Vector2[] points){
            painter.BeginPath();
            painter.MoveTo(points[0]);
            for (int i = 1; i < points.Length; i++) painter.LineTo(points[i]);
            painter.ClosePath();
        }
    }
}
