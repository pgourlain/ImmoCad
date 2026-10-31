using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using ImmoCad.Scene;
using Microsoft.Extensions.Logging;
using Silk.NET.OpenGL;

namespace ImmoCad.Desktop;

/// <summary>
/// OpenGL 3D view (Silk.NET inside an Avalonia OpenGlControlBase). Meshes come from ImmoCad.Scene; this class only uploads buffers and draws.
/// If the context cannot be created or a GL call fails, <see cref="Failed"/> is raised once so the window swaps to the software viewport.
/// </summary>
public sealed class GlViewport3D : OpenGlControlBase, IViewport3D, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>The GL surface draws no Avalonia background: without this the wheel and clicks could pass through it.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);
    private static readonly ILogger Log = App.Log.CreateLogger("GL");
    private MainViewModel? Vm => DataContext as MainViewModel;
    public ViewportController Controller { get; } = new();
    public Control Control => this;
    public event Action<string>? Failed;
    /// <summary>Walk mode: the one-line description of the object under the cursor (null when none), and where the cursor is.</summary>
    public event Action<string?, Point>? Hover;

    public bool Initialized { get; private set; }
    private GL? _gl; private uint _program; private bool _core;
    private int _uTarget, _uTrig, _uScale, _uDepth, _uColor, _uSelected, _uWalk, _uF, _uR, _uU, _uP, _uGhost, _uDissolve;
    private sealed record Buffers(uint Vao, uint Vbo, uint Ibo, uint Ebo, int Indices, int WireIndices);
    private readonly Dictionary<SceneObject, Buffers> _buffers = new(ReferenceEqualityComparer.Instance as IEqualityComparer<SceneObject>);
    private List<SceneObject> _uploaded = new();

    public GlViewport3D()
    {
        Focusable = true; ClipToBounds = true;
        Controller.Invalidate = RequestNextFrameRendering;
        Controller.Picked = (id, kind) => Vm?.Pick3D(id, kind);
        Controller.Handles = () => Vm?.Handles3D() ?? []; Controller.HandleDropped = (n, nd, mm) => Vm?.DropPipeEnd3D(n, nd, mm);
        Controller.WalkSource = () => Vm?.Walk; Controller.WalkGoTo = (x, y) => Vm?.Walk?.GoTo(new(x, y));
        Controller.Hovered = (hit, at) => Hover?.Invoke(hit is { } h ? Vm?.DescribeScene(h.Id, h.Kind) : null, at);
        Controller.PlaneToolActive = () => Vm?.IsPlaneTool == true; Controller.PlaneAltitudeM = () => (Vm?.CurrentLevel?.AltitudeMm ?? 0) / 1000.0; Controller.PlaneClicked = p => Vm?.Click3D(p);
    }

    public void Rebuild() { Controller.Rebuild(Vm); RequestNextFrameRendering(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { Focus(); var p = e.GetCurrentPoint(this); Controller.Down(e.GetPosition(this), p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed); e.Pointer.Capture(this); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { Controller.Up(e.GetPosition(this), Vm, Bounds.Width, Bounds.Height); e.Pointer.Capture(null); }
    protected override void OnPointerMoved(PointerEventArgs e) => Controller.Move(e.GetPosition(this));
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) => Controller.Wheel(e.Delta.Y);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = GL.GetApi(gl.GetProcAddress);
            var version = _gl.GetStringS(StringName.Version) ?? ""; var renderer = _gl.GetStringS(StringName.Renderer) ?? ""; var vendor = _gl.GetStringS(StringName.Vendor) ?? "";
            Log.LogInformation("OpenGL: {Renderer} / {Vendor} / {Version}", renderer, vendor, version);
            _core = !version.StartsWith("2.") && !version.StartsWith("1.") && !version.Contains("OpenGL ES");
            _program = Compile(_core); Initialized = true;
            _uTarget = _gl.GetUniformLocation(_program, "uTarget"); _uTrig = _gl.GetUniformLocation(_program, "uTrig"); _uScale = _gl.GetUniformLocation(_program, "uScale");
            _uDepth = _gl.GetUniformLocation(_program, "uDepth"); _uColor = _gl.GetUniformLocation(_program, "uColor"); _uSelected = _gl.GetUniformLocation(_program, "uSelected");
            _uWalk = _gl.GetUniformLocation(_program, "uWalk"); _uF = _gl.GetUniformLocation(_program, "uF"); _uR = _gl.GetUniformLocation(_program, "uR"); _uU = _gl.GetUniformLocation(_program, "uU");
            _uP = _gl.GetUniformLocation(_program, "uP"); _uGhost = _gl.GetUniformLocation(_program, "uGhost"); _uDissolve = _gl.GetUniformLocation(_program, "uDissolve");
        }
        catch (Exception ex) { Log.LogError(ex, "OpenGL initialisation failed, falling back to the software viewport"); Failed?.Invoke(ex.Message); }
    }

    protected override void OnOpenGlDeinit(GlInterface gl) { try { foreach (var b in _buffers.Values) Free(b); _buffers.Clear(); if (_gl != null && _program != 0) _gl.DeleteProgram(_program); } catch (Exception ex) { Log.LogWarning(ex, "GL cleanup"); } }

    protected override unsafe void OnOpenGlRender(GlInterface glIface, int fb)
    {
        if (_gl is not { } gl) return;
        try
        {
            var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark; var scaling = (TopLevel.GetTopLevel(this))?.RenderScaling ?? 1;
            int w = (int)(Bounds.Width * scaling), h = (int)(Bounds.Height * scaling); if (w < 1 || h < 1) return; Controller.Size(Bounds.Width, Bounds.Height);
            gl.Viewport(0, 0, (uint)w, (uint)h); gl.ClearColor(dark ? 0.12f : 0.965f, dark ? 0.125f : 0.97f, dark ? 0.14f : 0.976f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit); gl.Enable(EnableCap.DepthTest);
            if (!ReferenceEquals(_uploaded, Controller.Objects)) Upload(gl);
            var c = Controller.Camera; gl.UseProgram(_program);
            var scale = 2.0 / c.Distance; var min = Math.Min(w, h); var walk = Controller.WalkMode; var ghost = walk && Controller.Ghost;
            gl.Uniform1(_uWalk, walk ? 1f : 0f); gl.Uniform1(_uGhost, ghost ? 1f : 0f); gl.Uniform1(_uDissolve, 0f);
            if (walk)
            {   // perspective from the walker's eyes: the vertex shader divides by the depth along the view direction
                var wc = Controller.WalkCam; var f = wc.Forward; var r = wc.Right; var u = wc.Up;
                gl.Uniform3(_uTarget, (float)wc.Eye.X, (float)wc.Eye.Y, (float)wc.Eye.Z); gl.Uniform3(_uF, (float)f.X, (float)f.Y, (float)f.Z); gl.Uniform3(_uR, (float)r.X, (float)r.Y, (float)r.Z); gl.Uniform3(_uU, (float)u.X, (float)u.Y, (float)u.Z);
                gl.Uniform4(_uP, (float)(1 / wc.TanHalfFov), (float)((double)w / h), (float)wc.Near, (float)wc.Far);
            }
            else
            {
                gl.Uniform3(_uTarget, (float)c.Target.X, (float)c.Target.Y, (float)c.Target.Z);
                gl.Uniform4(_uTrig, (float)Math.Cos(c.Yaw), (float)Math.Sin(c.Yaw), (float)Math.Cos(c.Pitch), (float)Math.Sin(c.Pitch));
                gl.Uniform2(_uScale, (float)(scale * min / w), (float)(scale * min / h)); gl.Uniform1(_uDepth, (float)(1.0 / (c.Distance * 20)));
            }
            if (walk) { gl.Enable(EnableCap.PolygonOffsetFill); gl.PolygonOffset(1f, 1f); }
            foreach (var pass in new[] { false, true })              // opaque first, translucent (X-ray walls) after, without depth writes
            {
                gl.DepthMask(!pass); if (pass) { gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); } else gl.Disable(EnableCap.Blend);
                foreach (var o in Controller.Objects.Where(o => (o.Alpha < 0.999f) == pass))
                {
                    if (!_buffers.TryGetValue(o, out var b)) continue;
                    var col = o.Highlight ? (1f, 0.55f, 0f) : walk && o.Id == Controller.HoverId ? (1f, 0.75f, 0.3f) : o.Color;
                    gl.Uniform4(_uColor, col.Item1, col.Item2, col.Item3, o.Alpha); gl.Uniform1(_uSelected, Controller.Wireframe || o.Kind == SceneKind.Grid ? 0f : 1f);          // flat colour for wire
                    gl.Uniform1(_uDissolve, ghost && o.Kind is SceneKind.Wall or SceneKind.Floor or SceneKind.Ceiling ? 1f : 0f);          // walls fade around the eye in ghost mode
                    gl.BindVertexArray(b.Vao);
                    if (Controller.Wireframe || o.Kind == SceneKind.Grid) { gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, b.Ebo); gl.DrawElements(PrimitiveType.Lines, (uint)b.WireIndices, DrawElementsType.UnsignedInt, null); }
                    else
                    {
                        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, b.Ibo); gl.DrawElements(PrimitiveType.Triangles, (uint)b.Indices, DrawElementsType.UnsignedInt, null);
                        if (walk && o.Kind == SceneKind.Wall)
                        {   // dark edges: in a corridor a flat grey has no depth cues otherwise
                            gl.Uniform4(_uColor, 0.22f, 0.22f, 0.24f, 1f); gl.Uniform1(_uSelected, 0f);
                            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, b.Ebo); gl.DrawElements(PrimitiveType.Lines, (uint)b.WireIndices, DrawElementsType.UnsignedInt, null);
                        }
                    }
                }
            }
            if (walk) gl.Disable(EnableCap.PolygonOffsetFill);
            if (ghost)
            {   // pipes and equipment hidden behind a wall, a floor or a ceiling: drawn dimmed on top (only where something is in front of them)
                gl.DepthFunc(DepthFunction.Greater); gl.DepthMask(false); gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); gl.Uniform1(_uDissolve, 0f); gl.Uniform1(_uSelected, 0f);
                foreach (var o in Controller.Objects.Where(o => o.Kind is SceneKind.Pipe or SceneKind.Fitting or SceneKind.Component or SceneKind.Equipment))
                {
                    if (!_buffers.TryGetValue(o, out var b)) continue;
                    gl.Uniform4(_uColor, o.Color.Item1, o.Color.Item2, o.Color.Item3, 0.4f); gl.BindVertexArray(b.Vao);
                    gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, b.Ibo); gl.DrawElements(PrimitiveType.Triangles, (uint)b.Indices, DrawElementsType.UnsignedInt, null);
                }
                gl.DepthFunc(DepthFunction.Less);
            }
            gl.DepthMask(true); gl.Disable(EnableCap.Blend); gl.BindVertexArray(0);
            if (Controller.IsAnimating) RequestNextFrameRendering();
        }
        catch (Exception ex) { Log.LogError(ex, "OpenGL render failed"); Failed?.Invoke(ex.Message); }
    }

    private unsafe void Upload(GL gl)
    {
        foreach (var b in _buffers.Values) Free(b); _buffers.Clear();
        foreach (var o in Controller.Objects)
        {
            var m = o.Mesh; var verts = new float[m.VertexCount * 6];
            for (var i = 0; i < m.VertexCount; i++) { for (var k = 0; k < 3; k++) { verts[i * 6 + k] = m.Positions[i * 3 + k]; verts[i * 6 + 3 + k] = m.Normals.Count > i * 3 + k ? m.Normals[i * 3 + k] : 0; } }
            var grid = o.Kind == SceneKind.Grid;                                                         // a grid is line pairs already
            var idx = grid ? [] : m.Indices.Select(i => (uint)i).ToArray(); var wire = grid ? m.Indices.Select(i => (uint)i).ToArray() : WireEdges.Outline(m).SelectMany(e => new[] { (uint)e.A, (uint)e.B }).ToArray();
            var vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
            var vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo); fixed (float* p = verts) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * 4), p, BufferUsageARB.StaticDraw);
            var ibo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ibo); fixed (uint* p = idx) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * 4), p, BufferUsageARB.StaticDraw);
            var ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo); fixed (uint* p = wire) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(wire.Length * 4), p, BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.EnableVertexAttribArray(0); gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 24, (void*)0);
            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 24, (void*)12);
            _buffers[o] = new Buffers(vao, vbo, ibo, ebo, idx.Length, wire.Length);
        }
        gl.BindVertexArray(0); _uploaded = Controller.Objects;
    }

    private void Free(Buffers b) { if (_gl == null) return; _gl.DeleteBuffer(b.Vbo); _gl.DeleteBuffer(b.Ibo); _gl.DeleteBuffer(b.Ebo); _gl.DeleteVertexArray(b.Vao); }

    private uint Compile(bool core)
    {
        var gl = _gl!;
        // Same projection as ImmoCad.Scene.OrbitCamera.Project, evaluated in the vertex shader; one fixed world-space light.
        var header = core ? "#version 150\n" : "#version 120\n"; var vin = core ? "in" : "attribute"; var vout = core ? "out" : "varying"; var fin = core ? "in" : "varying";
        var vs = header + $@"{vin} vec3 aPos; {vin} vec3 aNorm; uniform vec3 uTarget; uniform vec4 uTrig; uniform vec2 uScale; uniform float uDepth; uniform float uWalk; uniform vec3 uF; uniform vec3 uR; uniform vec3 uU; uniform vec4 uP; {vout} vec3 vN; {vout} vec3 vW;
void main() {{ vec3 d = aPos - uTarget;
  if (uWalk > 0.5) {{ float dep = dot(d, uF); float a = (uP.w + uP.z) / (uP.w - uP.z); float b = -2.0 * uP.w * uP.z / (uP.w - uP.z); gl_Position = vec4(dot(d, uR) * uP.x / uP.y, dot(d, uU) * uP.x, a * dep + b, dep); }}
  else {{ float rx = d.x*uTrig.x + d.y*uTrig.y; float ry = -d.x*uTrig.y + d.y*uTrig.x;
    float sy = d.z*uTrig.z + ry*uTrig.w; float depth = ry*uTrig.z - d.z*uTrig.w; gl_Position = vec4(rx*uScale.x, sy*uScale.y, depth*uDepth, 1.0); }}
  vN = aNorm; vW = aPos; }}";
        var fs = header + (core ? "out vec4 frag; " : "") + $@"{fin} vec3 vN; {fin} vec3 vW; uniform vec4 uColor; uniform float uSelected; uniform float uWalk; uniform float uGhost; uniform float uDissolve; uniform vec3 uTarget;
float bayer(vec2 p) {{ vec2 q = mod(floor(p), 4.0); float a = mod(q.x, 2.0); float b = mod(q.y, 2.0); float c = floor(q.x / 2.0); float d = floor(q.y / 2.0);
  float m1 = 2.0*a + 3.0*b - 4.0*a*b; float m2 = 2.0*c + 3.0*d - 4.0*c*d; return (4.0*m1 + m2) / 16.0; }}
void main() {{
  float dist = length(vW - uTarget);
  if (uWalk > 0.5 && uGhost > 0.5 && uDissolve > 0.5) {{ float f = clamp((dist - 0.5) / 1.5, 0.0, 1.0); if (f <= bayer(gl_FragCoord.xy)) discard; }}
  float lam = 1.0;
  if (uSelected > 0.5) {{
    if (uWalk > 0.5) {{ vec3 v = (uTarget - vW) / max(dist, 0.001); float ndl = abs(dot(normalize(vN + vec3(0.00001)), v)); lam = (0.30 + 0.70*ndl) * (0.45 + 0.55 / (1.0 + 0.02*dist*dist)); }}
    else lam = abs(dot(normalize(vN + vec3(0.00001)), normalize(vec3(0.4,-0.5,0.8))))*0.65+0.35;
  }}
  vec4 c = vec4(uColor.rgb*lam, uColor.a); {(core ? "frag = c;" : "gl_FragColor = c;")} }}";
        uint Sh(ShaderType t, string src) { var s = gl.CreateShader(t); gl.ShaderSource(s, src); gl.CompileShader(s); gl.GetShader(s, ShaderParameterName.CompileStatus, out var ok); if (ok == 0) throw new InvalidOperationException(gl.GetShaderInfoLog(s)); return s; }
        var v = Sh(ShaderType.VertexShader, vs); var f = Sh(ShaderType.FragmentShader, fs); var p = gl.CreateProgram(); gl.AttachShader(p, v); gl.AttachShader(p, f);
        gl.BindAttribLocation(p, 0, "aPos"); gl.BindAttribLocation(p, 1, "aNorm"); gl.LinkProgram(p); gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out var linked); if (linked == 0) throw new InvalidOperationException(gl.GetProgramInfoLog(p));
        gl.DeleteShader(v); gl.DeleteShader(f); return p;
    }
}
