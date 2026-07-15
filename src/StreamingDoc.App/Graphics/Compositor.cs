using System.Diagnostics;
using System.Runtime.InteropServices;
using StreamingDoc.App.Model;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StreamingDoc.App.Graphics;

public enum TransitionType { Cut, Fade, Slide }

/// <summary>
/// Compone gli scene item su un canvas a risoluzione fissa, gestisce transizioni (cut/fade) e
/// studio mode, e disegna il risultato in una texture condivisa (PreviewTarget) mostrata da WPF
/// via D3DImage. Niente swapchain/HwndHost.
/// </summary>
public sealed class Compositor : IDisposable
{
    public int CanvasWidth { get; }
    public int CanvasHeight { get; }
    public int PreviewWidth => _pw;
    public int PreviewHeight => _ph;

    // Fit (px preview-texture) del pannello di editing: per overlay e mapping del mouse.
    public double EditScale { get; private set; } = 1;
    public double EditOffsetX { get; private set; }
    public double EditOffsetY { get; private set; }

    private const string Hlsl = @"
Texture2D tex : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID) {
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
cbuffer P : register(b0) {
    float opacity;
    float chromaOn;
    float similarity;
    float smoothness;
    float3 keyColor;
    float pad;
};
// RGB -> YCbCr (luma + crominanza). La separazione tonalita'/saturazione dalla
// luminanza rende il chroma key piu' preciso vs distanza RGB euclidea.
float2 chroma(float3 c) {
    float y = dot(c, float3(0.299, 0.587, 0.114));
    return float2((c.b - y) * 0.565, (c.r - y) * 0.713); // (Cb, Cr)
}
float4 PS(VSOut i) : SV_TARGET {
    float4 c = tex.Sample(smp, i.uv);
    if (chromaOn > 0.5) {
        float d = distance(chroma(c.rgb), chroma(keyColor)); // solo crominanza, no luma
        c.a *= smoothstep(similarity, similarity + smoothness, d);
    }
    c.a *= opacity;
    return c;
}";

    private readonly GraphicsDevice _gd;
    private ID3D11Texture2D _previewTex = null!;
    private ID3D11RenderTargetView _previewRtv = null!;
    private ID3D11Texture2D _previewStaging = null!;
    private readonly byte[] _previewPixels;
    private readonly object _pixLock = new();

    private readonly Canvas _main;  // program (registrato)
    private readonly Canvas _tmp;   // scena entrante durante il fade
    private readonly Canvas _prev;  // preview (studio mode)

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11BlendState _fadeBlend;
    private readonly ID3D11Texture2D _selTex;
    private readonly ID3D11ShaderResourceView _selSrv;
    private readonly ID3D11Texture2D _dimTex;     // overlay scuro semitrasparente fuori-canvas
    private readonly ID3D11ShaderResourceView _dimSrv;
    private readonly ID3D11Texture2D _boundTex;   // linea bordo canvas
    private readonly ID3D11ShaderResourceView _boundSrv;
    private readonly ID3D11Buffer _cb;

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterParams
    {
        public float Opacity, ChromaOn, Similarity, Smoothness;
        public float KeyR, KeyG, KeyB, Pad;
    }

    private readonly int _pw, _ph;
    private readonly object _sceneLock = new();
    private Scene? _program, _edit;
    private SceneItem? _selected;
    private bool _studio;

    private TransitionType _transition = TransitionType.Fade;
    private double _transitionMs = 350;
    private bool _fading;
    private Scene? _fadeFrom, _fadeTo;
    private readonly Stopwatch _fadeClock = new();

    private ID3D11Texture2D? _staging;
    private Nv12Converter? _nv12;
    private IFrameSink? _sink;
    private readonly Stopwatch _sinkClock = new();

    // Throttle del readback per registrazione/stream al fps target (non a ogni frame di render).
    private readonly Stopwatch _sinkThrottle = Stopwatch.StartNew();
    private double _sinkIntervalMs = 1000.0 / 60;
    public double SinkFps { set { _sinkIntervalMs = value <= 0 ? 0 : 1000.0 / value; } }

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _frameReady;
    private long _frameCount;
    public long FrameCount => _frameCount;

    // Throttle del readback preview: il compositing gira a ~60fps ma l'anteprima WPF
    // si aggiorna a ~30fps per dimezzare la copia VRAM->RAM (costo CPU principale).
    private readonly Stopwatch _previewClock = Stopwatch.StartNew();
    private double _previewIntervalMs = 1000.0 / 30;
    public double PreviewFps { set { _previewIntervalMs = value <= 0 ? 0 : 1000.0 / value; } }

    // Modalità anteprima zero-copy (D3DImage): se impostata, copia il preview nella texture
    // condivisa D3D11 (GPU->GPU) e segnala l'UI, invece del readback in RAM.
    private ID3D11Texture2D? _sharedPreview;
    private volatile bool _sharedReady;
    public void SetSharedPreview(ID3D11Texture2D? target) { lock (_gd.ContextLock) _sharedPreview = target; }
    public bool TakeSharedReady() { if (!_sharedReady) return false; _sharedReady = false; return true; }

    public object SceneLock => _sceneLock;

    private sealed class Canvas : IDisposable
    {
        public ID3D11Texture2D Tex = null!;
        public ID3D11RenderTargetView Rtv = null!;
        public ID3D11ShaderResourceView Srv = null!;
        public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Tex.Dispose(); }
    }

    public Compositor(GraphicsDevice gd, int canvasWidth = 1920, int canvasHeight = 1080)
    {
        _gd = gd;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;

        // Anteprima a risoluzione ridotta: meno pixel = meno copia VRAM->RAM per frame.
        _pw = Math.Min(960, canvasWidth);
        _ph = Math.Max(1, _pw * canvasHeight / canvasWidth);

        var ptd = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_pw, (uint)_ph, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default);
        _previewTex = _gd.Device.CreateTexture2D(ptd);
        _previewRtv = _gd.Device.CreateRenderTargetView(_previewTex);
        _previewStaging = _gd.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)_pw, (uint)_ph, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _previewPixels = new byte[_pw * _ph * 4];

        _main = MakeCanvas();
        _tmp = MakeCanvas();
        _prev = MakeCanvas();

        Compiler.Compile(Hlsl, "VS", "comp.hlsl", "vs_5_0", out Blob vsBlob, out Blob? vsErr).CheckError();
        Compiler.Compile(Hlsl, "PS", "comp.hlsl", "ps_5_0", out Blob psBlob, out Blob? psErr).CheckError();
        _vs = _gd.Device.CreateVertexShader(vsBlob.AsBytes());
        _ps = _gd.Device.CreatePixelShader(psBlob.AsBytes());
        vsBlob.Dispose(); psBlob.Dispose(); vsErr?.Dispose(); psErr?.Dispose();

        _sampler = _gd.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _blend = _gd.Device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha));

        var fb = new BlendDescription();
        fb.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.BlendFactor,
            DestinationBlend = Blend.InverseBlendFactor,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.BlendFactor,
            DestinationBlendAlpha = Blend.InverseBlendFactor,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _fadeBlend = _gd.Device.CreateBlendState(fb);

        _selTex = Create1x1(0, 200, 255);
        _selSrv = _gd.Device.CreateShaderResourceView(_selTex);
        _dimTex = Create1x1a(0, 0, 0, 140);          // nero ~55% per attenuare il fuori-canvas
        _dimSrv = _gd.Device.CreateShaderResourceView(_dimTex);
        _boundTex = Create1x1a(150, 160, 175, 230);  // linea bordo canvas
        _boundSrv = _gd.Device.CreateShaderResourceView(_boundTex);

        _cb = _gd.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 32,
            BindFlags = BindFlags.ConstantBuffer,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
    }

    private void SetFilter(ID3D11DeviceContext ctx, FilterParams p)
    {
        var map = ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, map.DataPointer, false);
        ctx.Unmap(_cb, 0);
    }

    private static readonly FilterParams DefaultFilter = new() { Opacity = 1f };

    private static FilterParams MakeFilter(SceneItem it) => new()
    {
        Opacity = (float)it.Opacity,
        ChromaOn = it.ChromaKey ? 1f : 0f,
        Similarity = (float)it.ChromaSimilarity,
        Smoothness = (float)it.ChromaSmoothness,
        KeyR = (float)it.ChromaR,
        KeyG = (float)it.ChromaG,
        KeyB = (float)it.ChromaB,
    };

    private Canvas MakeCanvas()
    {
        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)CanvasWidth, (uint)CanvasHeight,
            1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default);
        var tex = _gd.Device.CreateTexture2D(desc);
        return new Canvas { Tex = tex, Rtv = _gd.Device.CreateRenderTargetView(tex), Srv = _gd.Device.CreateShaderResourceView(tex) };
    }

    private ID3D11Texture2D Create1x1(byte r, byte g, byte b) => Create1x1a(r, g, b, 255);

    private ID3D11Texture2D Create1x1a(byte r, byte g, byte b, byte a)
    {
        byte[] px = { b, g, r, a };
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, 1, 1, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable);
            return _gd.Device.CreateTexture2D(desc, new[] { new SubresourceData(handle.AddrOfPinnedObject(), 4u) });
        }
        finally { handle.Free(); }
    }

    // ---- API ----

    public void SetScene(Scene? scene) { lock (_sceneLock) { _program = scene; _edit = scene; _fading = false; } }
    public void SetEditScene(Scene? scene) { lock (_sceneLock) _edit = scene; }
    public void SetStudio(bool on) { lock (_sceneLock) _studio = on; }
    public void SetTransition(TransitionType type, double ms) { lock (_sceneLock) { _transition = type; _transitionMs = Math.Max(1, ms); } }
    public void SetSelected(SceneItem? item) { lock (_sceneLock) _selected = item; }

    public void TransitionTo(Scene? scene)
    {
        lock (_sceneLock)
        {
            if (ReferenceEquals(scene, _program)) return;
            if (_transition == TransitionType.Cut || _program is null) { _program = scene; _fading = false; return; }
            _fadeFrom = _program; _fadeTo = scene; _fading = true; _fadeClock.Restart();
        }
    }

    public void SetSink(IFrameSink? sink)
    {
        lock (_gd.ContextLock) { _sink = sink; if (sink is not null) _sinkClock.Restart(); }
    }

    /// <summary>True una volta per ogni frame nuovo (consumato dal thread UI per AddDirtyRect).</summary>
    public bool TakeFrameReady()
    {
        if (!_frameReady) return false;
        _frameReady = false;
        return true;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Compositor", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop() { _running = false; _thread?.Join(2000); _thread = null; }

    // Cap massimo di render (di sicurezza). La cadenza reale è ADATTIVA: il loop gira solo
    // alla velocità del consumatore più veloce attivo (anteprima ~30fps, sink al suo fps).
    // Così su CPU deboli non si rende a 60 quando bastano 30: dimezza il lavoro a vuoto.
    private double _renderIntervalMs = 1000.0 / 60;
    public double RenderFps { set { _renderIntervalMs = value <= 0 ? 0 : 1000.0 / Math.Max(1, value); } }

    // Intervallo effettivo: max(anteprima, sink-se-attivo). Niente render inutili oltre il bisogno.
    private double EffectiveIntervalMs()
    {
        double iv = _previewIntervalMs > 0 ? _previewIntervalMs : 1000.0 / 30;
        if (_sink is not null && _sinkIntervalMs > 0 && _sinkIntervalMs < iv) iv = _sinkIntervalMs;
        if (iv < _renderIntervalMs) iv = _renderIntervalMs; // rispetta il cap massimo
        return iv;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);

    private void Loop()
    {
        // Risoluzione timer a 1ms: senza questo Thread.Sleep arrotonda al tick di sistema (~15.6ms),
        // quindi Sleep(31) dorme ~47ms -> il loop cappa a ~20fps anche se RenderFrame e' <2ms.
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            double next = sw.Elapsed.TotalMilliseconds;
            while (_running)
            {
                try { RenderFrame(); }
                catch { }

                double interval = EffectiveIntervalMs();
                next += interval;
                // Pacing preciso: Sleep grezzo fino a ~1ms dalla scadenza, poi spin sul tratto finale.
                double wait = next - sw.Elapsed.TotalMilliseconds;
                if (wait > 2) Thread.Sleep((int)(wait - 1));
                while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(64);
                if (sw.Elapsed.TotalMilliseconds - next > interval) next = sw.Elapsed.TotalMilliseconds; // troppo in ritardo: riallinea
            }
        }
        finally { timeEndPeriod(1); }
    }

    private void RenderFrame()
    {
        lock (_gd.ContextLock)
        {
            var ctx = _gd.Context;
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetSampler(0, _sampler);
            ctx.PSSetConstantBuffer(0, _cb);

            ComposeProgram(ctx);

            bool studio; Scene? edit;
            lock (_sceneLock) { studio = _studio; edit = _edit; }
            if (studio) DrawSceneTo(ctx, edit, _prev.Rtv);

            WriteToSink(ctx);
            // Il convertitore NV12 cambia VS/PS/sampler: ripristina la pipeline del compositor.
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetSampler(0, _sampler);
            ctx.PSSetConstantBuffer(0, _cb);
            BlitToPreview(ctx, studio);
            DrawSelectionOverlay(ctx);

            // Aggiornamento anteprima al rate target, non a ogni frame.
            if (_previewClock.Elapsed.TotalMilliseconds >= _previewIntervalMs)
            {
                _previewClock.Restart();
                if (_sharedPreview is not null)
                {
                    // Zero-copy: copia GPU->GPU nella texture condivisa + flush, poi segnala l'UI.
                    ctx.CopyResource(_sharedPreview, _previewTex);
                    ctx.Flush();
                    _sharedReady = true;
                }
                else
                {
                    ReadbackPreview(ctx); // fallback: copia VRAM->RAM per WriteableBitmap
                    _frameReady = true;
                }
            }

            _frameCount++;
        }
    }

    private void ReadbackPreview(ID3D11DeviceContext ctx)
    {
        ctx.CopyResource(_previewStaging, _previewTex);
        var map = ctx.Map(_previewStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rp = (int)map.RowPitch, dst = _pw * 4;
            lock (_pixLock)
                for (int y = 0; y < _ph; y++)
                    Marshal.Copy(IntPtr.Add(map.DataPointer, y * rp), _previewPixels, y * dst, dst);
        }
        finally { ctx.Unmap(_previewStaging, 0); }
    }

    /// <summary>Copia l'ultimo frame di preview (BGRA) nel buffer di un WriteableBitmap.</summary>
    public void CopyPreviewPixels(IntPtr dest, int destStride)
    {
        int src = _pw * 4, n = Math.Min(src, destStride);
        lock (_pixLock)
            for (int y = 0; y < _ph; y++)
                Marshal.Copy(_previewPixels, y * src, IntPtr.Add(dest, y * destStride), n);
    }

    private void ComposeProgram(ID3D11DeviceContext ctx)
    {
        bool fading; Scene? from, to, prog; double t = 0; TransitionType type;
        lock (_sceneLock)
        {
            fading = _fading; from = _fadeFrom; to = _fadeTo; prog = _program; type = _transition;
            if (fading)
            {
                t = _fadeClock.Elapsed.TotalMilliseconds / _transitionMs;
                if (t >= 1) { _program = _fadeTo; _fading = false; fading = false; prog = _fadeTo; }
            }
        }

        if (!fading)
        {
            DrawSceneTo(ctx, prog, _main.Rtv);
            return;
        }

        DrawSceneTo(ctx, from, _main.Rtv);
        DrawSceneTo(ctx, to, _tmp.Rtv);
        ctx.OMSetRenderTargets(_main.Rtv);

        if (type == TransitionType.Slide)
        {
            // 'to' entra da destra sopra 'from' (offset viewport in X).
            ctx.OMSetBlendState(_blend);
            float x = (float)(CanvasWidth * (1.0 - Math.Min(1.0, t)));
            ctx.RSSetViewport(new Viewport(x, 0, CanvasWidth, CanvasHeight, 0, 1));
        }
        else
        {
            // Fade: dissolvenza incrociata con alpha costante = t.
            ctx.OMSetBlendState(_fadeBlend, new Color4((float)t, (float)t, (float)t, (float)t));
            ctx.RSSetViewport(new Viewport(0, 0, CanvasWidth, CanvasHeight, 0, 1));
        }

        ctx.PSSetShaderResource(0, _tmp.Srv);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
    }

    private void DrawSceneTo(ID3D11DeviceContext ctx, Scene? scene, ID3D11RenderTargetView rtv)
    {
        ctx.OMSetRenderTargets(rtv);
        ctx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
        ctx.OMSetBlendState(_blend);

        SceneItem[] items;
        lock (_sceneLock) items = scene is null ? Array.Empty<SceneItem>() : scene.Items.ToArray();

        // Disegna dal fondo della lista verso la cima: l'item in CIMA alla lista (indice 0) viene
        // disegnato per ultimo, quindi sta DAVANTI (convenzione OBS: cima lista = primo piano).
        for (int i = items.Length - 1; i >= 0; i--)
        {
            var item = items[i];
            if (!item.Visible) continue;
            var srv = item.Source.GetSrv();
            if (srv is null) continue;
            SetFilter(ctx, MakeFilter(item));
            ctx.RSSetViewport(new Viewport((float)item.X, (float)item.Y, (float)item.Width, (float)item.Height, 0f, 1f));
            ctx.PSSetShaderResource(0, srv);
            ctx.Draw(3, 0);
        }
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        SetFilter(ctx, DefaultFilter);
    }

    private void WriteToSink(ID3D11DeviceContext ctx)
    {
        var sink = _sink;
        if (sink is null) return;
        // Consegna al sink solo al rate target: evita readback inutili a 60fps quando si registra a 30.
        // Tolleranza -4ms: il loop e' gia' paced allo stesso intervallo del sink, quindi Elapsed cade
        // proprio intorno a _sinkIntervalMs; senza margine, il jitter fa scartare ~40% dei frame (beat
        // tra due clock a 30Hz) -> uscita a ~18fps invece di 30. Il margine rompe il beat.
        if (_sinkThrottle.Elapsed.TotalMilliseconds < _sinkIntervalMs - 4) return;
        _sinkThrottle.Restart();
        long ts = _sinkClock.Elapsed.Ticks;

        // Path preferito: conversione NV12 su GPU (niente sws_scale CPU, readback 1.5 byte/px).
        if (sink.WantsNv12)
        {
            _nv12 ??= new Nv12Converter(_gd, CanvasWidth, CanvasHeight);
            _nv12.ConvertAndDeliver(ctx, _main.Srv, sink, ts);
            return;
        }

        // Fallback: readback BGRA (4 byte/px), il sink converte via sws_scale.
        _staging ??= _gd.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)CanvasWidth, (uint)CanvasHeight, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        ctx.CopyResource(_staging, _main.Tex);
        var map = ctx.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { sink.WriteFrame(map.DataPointer, (int)map.RowPitch, CanvasWidth, CanvasHeight, ts); }
        finally { ctx.Unmap(_staging, 0); }
    }

    private void BlitToPreview(ID3D11DeviceContext ctx, bool studio)
    {
        ctx.OMSetRenderTargets(_previewRtv);
        ctx.ClearRenderTargetView(_previewRtv, new Color4(0.16f, 0.17f, 0.20f, 1f)); // grigio (non nero)
        ctx.OMSetBlendState(_blend);

        if (studio)
        {
            int half = _pw / 2;
            BlitInto(ctx, _prev.Srv, 0, 0, half - 3, _ph, editPane: true);
            BlitInto(ctx, _main.Srv, half + 3, 0, half - 3, _ph, editPane: false);
        }
        else
        {
            bool fading; Scene? prog;
            lock (_sceneLock) { fading = _fading; prog = _program; }
            if (fading)
                BlitInto(ctx, _main.Srv, 0, 0, _pw, _ph, editPane: true); // mostra la transizione (no overflow)
            else
                RenderEditScene(ctx, prog); // editing: mostra anche il fuori-canvas
        }
    }

    // Anteprima di editing: canvas con margine, le sorgenti possono uscire dal canvas (mostrate
    // attenuate), bordo canvas evidenziato. Il programma/registrazione restano tagliati al canvas.
    private void RenderEditScene(ID3D11DeviceContext ctx, Scene? scene)
    {
        float fit = Math.Min((float)_pw / CanvasWidth, (float)_ph / CanvasHeight);
        float scale = fit * 0.97f; // margine sottile per il fuori-canvas (poco bordo nero)
        float vpW = CanvasWidth * scale, vpH = CanvasHeight * scale;
        float vpX = (_pw - vpW) / 2f, vpY = (_ph - vpH) / 2f;
        EditScale = scale; EditOffsetX = vpX; EditOffsetY = vpY;

        SceneItem[] items;
        lock (_sceneLock) items = scene is null ? Array.Empty<SceneItem>() : scene.Items.ToArray();

        ctx.OMSetBlendState(_blend);
        for (int i = items.Length - 1; i >= 0; i--)
        {
            var it = items[i];
            if (!it.Visible) continue;
            var srv = it.Source.GetSrv();
            if (srv is null) continue;
            SetFilter(ctx, MakeFilter(it));
            ctx.RSSetViewport(new Viewport(vpX + (float)it.X * scale, vpY + (float)it.Y * scale,
                (float)it.Width * scale, (float)it.Height * scale, 0f, 1f));
            ctx.PSSetShaderResource(0, srv);
            ctx.Draw(3, 0);
        }
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
        SetFilter(ctx, DefaultFilter);

        // Attenua tutto cio' che e' fuori dal canvas (4 fasce attorno al canvas).
        ctx.PSSetShaderResource(0, _dimSrv);
        Quad(ctx, 0, 0, _pw, vpY);
        Quad(ctx, 0, vpY + vpH, _pw, _ph - (vpY + vpH));
        Quad(ctx, 0, vpY, vpX, vpH);
        Quad(ctx, vpX + vpW, vpY, _pw - (vpX + vpW), vpH);

        // Bordo del canvas.
        ctx.PSSetShaderResource(0, _boundSrv);
        const float t = 2f;
        Quad(ctx, vpX, vpY, vpW, t);
        Quad(ctx, vpX, vpY + vpH - t, vpW, t);
        Quad(ctx, vpX, vpY, t, vpH);
        Quad(ctx, vpX + vpW - t, vpY, t, vpH);
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
    }

    private void BlitInto(ID3D11DeviceContext ctx, ID3D11ShaderResourceView srv,
        float rx, float ry, float rw, float rh, bool editPane)
    {
        if (rw < 1 || rh < 1) return;
        float scale = Math.Min(rw / CanvasWidth, rh / CanvasHeight);
        float vpW = CanvasWidth * scale, vpH = CanvasHeight * scale;
        float vpX = rx + (rw - vpW) / 2f, vpY = ry + (rh - vpH) / 2f;

        if (editPane) { EditScale = scale; EditOffsetX = vpX; EditOffsetY = vpY; }

        ctx.RSSetViewport(new Viewport(vpX, vpY, vpW, vpH, 0f, 1f));
        ctx.PSSetShaderResource(0, srv);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
    }

    private void DrawSelectionOverlay(ID3D11DeviceContext ctx)
    {
        SceneItem? sel;
        lock (_sceneLock) sel = _selected;
        if (sel is null) return;

        float bx = (float)(EditOffsetX + sel.X * EditScale);
        float by = (float)(EditOffsetY + sel.Y * EditScale);
        float bw = (float)(sel.Width * EditScale);
        float bh = (float)(sel.Height * EditScale);

        ctx.PSSetShaderResource(0, _selSrv);
        const float t = 2f;
        Quad(ctx, bx, by, bw, t);
        Quad(ctx, bx, by + bh - t, bw, t);
        Quad(ctx, bx, by, t, bh);
        Quad(ctx, bx + bw - t, by, t, bh);
        const float hs = 8f;
        Quad(ctx, bx - hs / 2, by - hs / 2, hs, hs);
        Quad(ctx, bx + bw - hs / 2, by - hs / 2, hs, hs);
        Quad(ctx, bx - hs / 2, by + bh - hs / 2, hs, hs);
        Quad(ctx, bx + bw - hs / 2, by + bh - hs / 2, hs, hs);
        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
    }

    private void Quad(ID3D11DeviceContext ctx, float x, float y, float w, float h)
    {
        ctx.RSSetViewport(new Viewport(x, y, Math.Max(1f, w), Math.Max(1f, h), 0f, 1f));
        ctx.Draw(3, 0);
    }

    public void Dispose()
    {
        Stop();
        lock (_gd.ContextLock)
        {
            _selSrv.Dispose();
            _selTex.Dispose();
            _dimSrv.Dispose();
            _dimTex.Dispose();
            _boundSrv.Dispose();
            _boundTex.Dispose();
            _cb.Dispose();
            _fadeBlend.Dispose();
            _blend.Dispose();
            _sampler.Dispose();
            _vs.Dispose();
            _ps.Dispose();
            _main.Dispose();
            _tmp.Dispose();
            _prev.Dispose();
            _staging?.Dispose();
            _nv12?.Dispose();
            _previewRtv.Dispose();
            _previewTex.Dispose();
            _previewStaging.Dispose();
        }
    }
}
