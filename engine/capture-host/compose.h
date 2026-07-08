// compose.h — Chroma-Compose des capture-host (D1 Phase 2, 8.7.2026).
// 1:1-Port des Layer-Compose (layer.cpp kChromaKeyHlsl + SetupChromaPipeline +
// Per-Quad-Dispatch), nur dass das Ziel jetzt direkt ein Shared-Ring-Buffer-UAV ist
// (kein Intermediate nötig — der Layer macht seinerseits CopyResource in die
// UNORM_SRGB-Swapchain, gleiche TYPELESS-Familie wie bisher).
// Highlight-Zustand kommt nicht mehr aus Layer-Membern, sondern aus dem HL-SHM-Block.

#pragma once

#include "pch.h"
#include "beehive_shm.h"

#include <cstring>

namespace capturehost {

    // Wörtlich aus layer.cpp:57-101 — Magenta-Key + Opacity + Border-Highlight.
    constexpr const char* kChromaKeyHlsl = R"HLSL(
cbuffer config : register(b0) {
    float3 TransparentColor;
    float  Opacity;
    uint   RectX;
    uint   RectY;
    uint   RectW;
    uint   RectH;
    float  HighlightOn;
    float  BorderPx;
    float  _pad0;
    float  _pad_align;
    float3 HighlightColor;
    float  _pad1;
};
Texture2D in_texture : register(t0);
RWTexture2D<float4> out_texture : register(u0);

[numthreads(8, 8, 1)]
void main(uint2 tid : SV_DispatchThreadID)
{
    if (tid.x >= RectW || tid.y >= RectH) return;
    uint2 pos = uint2(RectX + tid.x, RectY + tid.y);

    if (HighlightOn > 0.5) {
        uint bx = (uint)BorderPx;
        if (tid.x < bx || tid.y < bx ||
            tid.x >= RectW - bx || tid.y >= RectH - bx) {
            out_texture[pos] = float4(HighlightColor, 1.0);
            return;
        }
    }

    float4 src = in_texture[pos];
    out_texture[pos] = float4(src.rgb * Opacity, src.a * Opacity);
}
)HLSL";

    // Spiegel der HLSL-cbuffer-Rows (layer.cpp:105-119).
    struct ChromaConstants {
        float    transparentColor[3];
        float    opacity;
        uint32_t rectX, rectY, rectW, rectH;
        float    highlightOn;
        float    borderPx;
        float    pad0, padAlign;
        float    highlightColor[3];
        float    pad1;
    };
    static_assert(sizeof(ChromaConstants) == 64, "ChromaConstants muss 64 Bytes sein");

    class ComposePipeline {
      public:
        bool init(ID3D11Device* device, std::string& err) {
            ComPtr<ID3DBlob> shaderBlob, errorBlob;
            HRESULT hr = D3DCompile(kChromaKeyHlsl, std::strlen(kChromaKeyHlsl),
                                    "chroma_key.hlsl", nullptr, nullptr, "main", "cs_5_0",
                                    D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &shaderBlob, &errorBlob);
            if (FAILED(hr)) {
                err = errorBlob ? (const char*)errorBlob->GetBufferPointer() : "(kein error blob)";
                return false;
            }
            hr = device->CreateComputeShader(shaderBlob->GetBufferPointer(),
                                             shaderBlob->GetBufferSize(), nullptr, &m_shader);
            if (FAILED(hr)) { err = "CreateComputeShader"; return false; }

            D3D11_BUFFER_DESC cbd{};
            cbd.ByteWidth      = sizeof(ChromaConstants);
            cbd.Usage          = D3D11_USAGE_DYNAMIC;
            cbd.BindFlags      = D3D11_BIND_CONSTANT_BUFFER;
            cbd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            hr = device->CreateBuffer(&cbd, nullptr, &m_cb);
            if (FAILED(hr)) { err = "CreateBuffer(CB)"; return false; }
            m_device = device;
            return true;
        }

        // Komponiert alle sichtbaren, passenden Quads von sourceTex nach targetUAV.
        // Rect-Bounds-Check gegen texW/H (Port von QuadRectFitsAtlas — WPF/WGC racen
        // bei Resize ein paar Frames; nicht-passende Rects werden still übersprungen).
        void run(ID3D11DeviceContext* ctx, ID3D11Texture2D* sourceTex,
                 ID3D11UnorderedAccessView* targetUAV,
                 const beehive::shm::FrameSlot& frame,
                 const beehive::shm::QuadSlot* slots, uint32_t slotCount,
                 const beehive::shm::HighlightSlot& hl,
                 uint32_t texW, uint32_t texH) {
            using namespace beehive::shm;

            // Source-SRV nach Pointer cachen (WGC rotiert Pool-Surfaces).
            if (sourceTex != m_srcCachedPtr) {
                m_srcSRV.Reset();
                D3D11_SHADER_RESOURCE_VIEW_DESC srvd{};
                srvd.Format              = DXGI_FORMAT_R8G8B8A8_UNORM;
                srvd.ViewDimension       = D3D11_SRV_DIMENSION_TEXTURE2D;
                srvd.Texture2D.MipLevels = 1;
                if (FAILED(m_device->CreateShaderResourceView(sourceTex, &srvd, &m_srcSRV)))
                    return;
                m_srcCachedPtr = sourceTex;
            }

            ID3D11ShaderResourceView* srvs[] = {m_srcSRV.Get()};
            ID3D11UnorderedAccessView* uavs[] = {targetUAV};
            ID3D11Buffer* cbs[] = {m_cb.Get()};
            ctx->CSSetShader(m_shader.Get(), nullptr, 0);
            ctx->CSSetShaderResources(0, 1, srvs);
            ctx->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
            ctx->CSSetConstantBuffers(0, 1, cbs);

            const bool dragOpacityValid = (hl.flags & 1u) != 0;

            for (uint32_t i = 0; i < slotCount; ++i) {
                const QuadSlot& s = slots[i];
                if (!s.visible) continue;
                if (s.rectW == 0 || s.rectH == 0) continue;
                if ((s.rectX + s.rectW) > texW || (s.rectY + s.rectH) > texH) continue;

                D3D11_MAPPED_SUBRESOURCE mapped{};
                if (FAILED(ctx->Map(m_cb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
                    continue;
                ChromaConstants* cb = (ChromaConstants*)mapped.pData;
                cb->transparentColor[0] = 1.0f;
                cb->transparentColor[1] = 0.0f;
                cb->transparentColor[2] = 1.0f;
                cb->rectX = s.rectX;
                cb->rectY = s.rectY;
                cb->rectW = s.rectW;
                cb->rectH = s.rectH;

                // Border-Stufung wie im Layer (5.6.2026 v3): Grab cyan 3px >
                // Hover weiß 2px > Place-Mode grün(gedämpft) 1px > aus.
                const bool isGrabbed = std::memcmp(s.id, hl.grabbedId, 16) == 0 &&
                                       hl.grabbedId[0] != '\0';
                const bool isHovered = !isGrabbed && hl.hoveredId[0] != '\0' &&
                                       std::memcmp(s.id, hl.hoveredId, 16) == 0;
                cb->opacity = (isGrabbed && dragOpacityValid) ? hl.dragOpacity : s.opacity;
                if (isGrabbed) {
                    cb->highlightOn = 1.0f; cb->borderPx = 3.0f;
                    cb->highlightColor[0] = 0.0f; cb->highlightColor[1] = 1.0f;
                    cb->highlightColor[2] = 1.0f;
                } else if (isHovered) {
                    cb->highlightOn = 1.0f; cb->borderPx = 2.0f;
                    cb->highlightColor[0] = 1.0f; cb->highlightColor[1] = 1.0f;
                    cb->highlightColor[2] = 1.0f;
                } else if (frame.placeModeOn != 0) {
                    cb->highlightOn = 1.0f; cb->borderPx = 1.0f;
                    cb->highlightColor[0] = 0.0f; cb->highlightColor[1] = 0.55f;
                    cb->highlightColor[2] = 0.0f;
                } else {
                    cb->highlightOn = 0.0f; cb->borderPx = 0.0f;
                    cb->highlightColor[0] = 0.0f; cb->highlightColor[1] = 0.0f;
                    cb->highlightColor[2] = 0.0f;
                }
                cb->pad0 = cb->padAlign = cb->pad1 = 0.0f;
                ctx->Unmap(m_cb.Get(), 0);

                ctx->Dispatch((s.rectW + 7) / 8, (s.rectH + 7) / 8, 1);
            }

            // Bindings lösen (sonst Konflikte bei nachfolgenden Operationen).
            ID3D11ShaderResourceView* nullSRV[] = {nullptr};
            ID3D11UnorderedAccessView* nullUAV[] = {nullptr};
            ctx->CSSetShaderResources(0, 1, nullSRV);
            ctx->CSSetUnorderedAccessViews(0, 1, nullUAV, nullptr);
            ctx->CSSetShader(nullptr, nullptr, 0);
        }

        void invalidateSourceCache() {
            m_srcCachedPtr = nullptr;
            m_srcSRV.Reset();
        }

      private:
        ID3D11Device* m_device = nullptr;
        ComPtr<ID3D11ComputeShader> m_shader;
        ComPtr<ID3D11Buffer> m_cb;
        ComPtr<ID3D11ShaderResourceView> m_srcSRV;
        ID3D11Texture2D* m_srcCachedPtr = nullptr;
    };

} // namespace capturehost
