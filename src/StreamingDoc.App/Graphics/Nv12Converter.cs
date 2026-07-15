using System;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StreamingDoc.App.Graphics;

/// <summary>
/// Converte il canvas compositato (BGRA) in NV12 sulla GPU: piano Y a piena risoluzione (R8) e
/// piano UV interlacciato a meta' risoluzione (R8G8), coefficienti BT.601. Evita la conversione
/// colore su CPU (sws_scale) e dimezza la banda di readback (1.5 vs 4 byte/pixel).
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    private const string Hlsl = @"
Texture2D tex : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID) {
    VSOut o; float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv; o.pos = float4(uv * float2(2,-2) + float2(-1,1), 0, 1); return o;
}
float PSY(VSOut i) : SV_TARGET {
    float3 c = tex.Sample(smp, i.uv).rgb;
    return 0.257*c.r + 0.504*c.g + 0.098*c.b + 0.0627451; // Y' BT.601 limited range (= sws default)
}
float2 PSUV(VSOut i) : SV_TARGET {
    float3 c = tex.Sample(smp, i.uv).rgb;
    float u = -0.148*c.r - 0.291*c.g + 0.439*c.b + 0.5019608; // Cb
    float v =  0.439*c.r - 0.368*c.g - 0.071*c.b + 0.5019608; // Cr
    return float2(u, v);
}";

    private const int Ring = 3; // readback pipelinato: mappa il frame di 2 cicli fa (GPU gia' finito) -> niente stallo
    private readonly int _w, _h, _cw, _ch;
    private readonly ID3D11Texture2D _yTex, _uvTex;
    private readonly ID3D11Texture2D[] _yStaging = new ID3D11Texture2D[Ring];
    private readonly ID3D11Texture2D[] _uvStaging = new ID3D11Texture2D[Ring];
    private readonly long[] _slotTs = new long[Ring];
    private readonly bool[] _slotFull = new bool[Ring];
    private int _slot;
    private readonly ID3D11RenderTargetView _yRtv, _uvRtv;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psY, _psUV;
    private readonly ID3D11SamplerState _sampler;

    public Nv12Converter(GraphicsDevice gd, int width, int height)
    {
        _w = width; _h = height; _cw = width / 2; _ch = height / 2;
        var dev = gd.Device;

        _yTex = dev.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)_w, (uint)_h, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default));
        _yRtv = dev.CreateRenderTargetView(_yTex);
        for (int i = 0; i < Ring; i++)
            _yStaging[i] = dev.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)_w, (uint)_h, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        _uvTex = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8_UNorm, (uint)_cw, (uint)_ch, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default));
        _uvRtv = dev.CreateRenderTargetView(_uvTex);
        for (int i = 0; i < Ring; i++)
            _uvStaging[i] = dev.CreateTexture2D(new Texture2DDescription(Format.R8G8_UNorm, (uint)_cw, (uint)_ch, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        Compiler.Compile(Hlsl, "VS", "nv12.hlsl", "vs_5_0", out Blob vsB, out Blob? vsE).CheckError();
        Compiler.Compile(Hlsl, "PSY", "nv12.hlsl", "ps_5_0", out Blob yB, out Blob? yE).CheckError();
        Compiler.Compile(Hlsl, "PSUV", "nv12.hlsl", "ps_5_0", out Blob uB, out Blob? uE).CheckError();
        _vs = dev.CreateVertexShader(vsB.AsBytes());
        _psY = dev.CreatePixelShader(yB.AsBytes());
        _psUV = dev.CreatePixelShader(uB.AsBytes());
        vsB.Dispose(); yB.Dispose(); uB.Dispose(); vsE?.Dispose(); yE?.Dispose(); uE?.Dispose();

        _sampler = dev.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
    }

    /// <summary>
    /// Renderizza i piani Y e UV dalla SRV sorgente, mappa lo staging e consegna NV12 al sink.
    /// Da chiamare sul thread di render con il ContextLock gia' preso.
    /// </summary>
    public void ConvertAndDeliver(ID3D11DeviceContext ctx, ID3D11ShaderResourceView srcSrv, IFrameSink sink, long ts)
    {
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetSampler(0, _sampler);
        ctx.OMSetBlendState(null); // opaco: scrive direttamente

        // IMPORTANTE: stacca prima il render target sorgente (_main era RTV), poi bind come SRV,
        // altrimenti D3D11 forza la SRV a null per l'hazard read/write e si campiona nero.
        ctx.OMSetRenderTargets(_yRtv);
        ctx.PSSetShaderResource(0, srcSrv);
        ctx.RSSetViewport(new Viewport(0, 0, _w, _h, 0, 1));
        ctx.PSSetShader(_psY);
        ctx.Draw(3, 0);

        ctx.OMSetRenderTargets(_uvRtv);
        ctx.RSSetViewport(new Viewport(0, 0, _cw, _ch, 0, 1));
        ctx.PSSetShader(_psUV);
        ctx.Draw(3, 0);

        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);

        // Readback PIPELINATO: copia nel slot corrente, poi mappa il slot piu' vecchio (copiato Ring-1
        // cicli fa, GPU gia' completata) -> il Map non aspetta la GPU -> il loop regge il frame rate pieno.
        int w = _slot;
        ctx.CopyResource(_yStaging[w], _yTex);
        ctx.CopyResource(_uvStaging[w], _uvTex);
        _slotTs[w] = ts;
        _slotFull[w] = true;
        _slot = (w + 1) % Ring;

        int r = _slot; // slot piu' vecchio (verra' sovrascritto al prossimo giro)
        if (!_slotFull[r]) return; // warmup: primi Ring-1 frame non ancora pronti

        var ymap = ctx.Map(_yStaging[r], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var umap = ctx.Map(_uvStaging[r], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { sink.WriteFrameNv12(ymap.DataPointer, (int)ymap.RowPitch, umap.DataPointer, (int)umap.RowPitch, _w, _h, _slotTs[r]); }
        finally { ctx.Unmap(_yStaging[r], 0); ctx.Unmap(_uvStaging[r], 0); }
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _psUV.Dispose(); _psY.Dispose(); _vs.Dispose();
        _uvRtv.Dispose(); _uvTex.Dispose();
        _yRtv.Dispose(); _yTex.Dispose();
        for (int i = 0; i < Ring; i++) { _yStaging[i]?.Dispose(); _uvStaging[i]?.Dispose(); }
    }
}
