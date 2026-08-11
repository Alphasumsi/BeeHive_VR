// Adapted verbatim from Mbucchia OpenXR-Layer-Template, branch examples/overlay-desktop-window.
// Copyright(c) 2022-2023 Matthieu Bucchianeri.
// Borrows code from StereoKit (Nick Klingensmith) and Win32CaptureSample (Robert Mikhayelyan).

#pragma once

#include <chrono>

namespace openxr_api_layer::capture {

    namespace {

        // Alternative to windows.graphics.directx.direct3d11.interop.h
        extern "C" {
        HRESULT __stdcall CreateDirect3D11DeviceFromDXGIDevice(::IDXGIDevice* dxgiDevice,
                                                               ::IInspectable** graphicsDevice);

        HRESULT __stdcall CreateDirect3D11SurfaceFromDXGISurface(::IDXGISurface* dgxiSurface,
                                                                 ::IInspectable** graphicsSurface);
        }

        // https://gist.github.com/kennykerr/15a62c8218254bc908de672e5ed405fa
        struct __declspec(uuid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")) IDirect3DDXGIInterfaceAccess : ::IUnknown {
            virtual HRESULT __stdcall GetInterface(GUID const& id, void** object) = 0;
        };

    } // namespace

    struct ICaptureWindow {
        virtual ~ICaptureWindow() = default;

        virtual ID3D11Texture2D* getSurface() = 0;

        // C (3.7.2026): minimaler Abstand zwischen WGC-Frame-Pulls in Nanosekunden.
        // 0 = jeder getSurface()-Aufruf pullt (Alt-Verhalten). >0 = Capture-Rate gedeckelt.
        virtual void setMinPullIntervalNs(int64_t ns) = 0;

        // Cursor (11.8.2026): WGC-Cursor zur Laufzeit an/aus. Der Ctor lässt ihn aus;
        // der capture-host schaltet ihn nur EIN, solange das Ziel-Fenster im Vordergrund
        // ist (background/minimiert = nie Vordergrund → Cursor bleibt aus).
        virtual void setCursorCaptureEnabled(bool enabled) = 0;

        // C2 (8.7.2026): drosselt die Capture PRODUCER-seitig via
        // GraphicsCaptureSession.MinUpdateInterval (Win11 24H2+, Build 26100+) — der DWM
        // erzeugt dann wirklich nur alle N ns einen Frame, statt (24H2-Verhalten, von
        // OpenKneeboard v1.10.16 dokumentiert) permanent zu capturen. C drosselt nur unser
        // ABHOLEN; die DWM-Blits in die Pool-Surfaces auf iRacings Device liefen weiter —
        // C2 nimmt genau die weg. Rückgabe false = API nicht verfügbar (älteres Windows).
        virtual bool setMinUpdateIntervalNs(int64_t ns) = 0;
    };

    struct CaptureWindowWinRT : ICaptureWindow {
        CaptureWindowWinRT(ID3D11Device* device, HWND window) {
            ComPtr<IDXGIDevice> dxgiDevice;
            CHECK_HRCMD(device->QueryInterface(IID_PPV_ARGS(dxgiDevice.ReleaseAndGetAddressOf())));
            ComPtr<IInspectable> object;
            CHECK_HRCMD(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), object.GetAddressOf()));
            CHECK_HRCMD(
                object->QueryInterface(winrt::guid_of<winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>(),
                                       winrt::put_abi(m_interopDevice)));

            auto interop_factory = winrt::get_activation_factory<winrt::Windows::Graphics::Capture::GraphicsCaptureItem,
                                                                 IGraphicsCaptureItemInterop>();
            CHECK_HRCMD(interop_factory->CreateForWindow(
                window,
                winrt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(),
                winrt::put_abi(m_item)));

            m_lastSize = m_item.Size();
            m_framePool = winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(
                m_interopDevice,
                static_cast<winrt::Windows::Graphics::DirectX::DirectXPixelFormat>(DXGI_FORMAT_R8G8B8A8_UNORM),
                2,
                m_lastSize);
            m_session = m_framePool.CreateCaptureSession(m_item);

            // 19.7.2026 (Window-Capture-Entkopplung): gelben WGC-Rahmen + Mauszeiger
            // abschalten. Beim gecloakten Atlas-Fenster war das egal, seit der
            // capture-host aber ECHTE, sichtbare Fenster captured (Bloops-Overlay &
            // Co.) landeten sonst Rahmen und Cursor mit im VR-Overlay.
            // Beides best-effort: IsCursorCaptureEnabled gibt es ab Win10 2004,
            // IsBorderRequired erst ab Win11 22H2 — auf älteren Systemen wirft der
            // Setter, das ist kein Fehler (nur ein Schönheitsfehler im Bild).
            try {
                m_session.IsCursorCaptureEnabled(false);
            } catch (const winrt::hresult_error&) {
                // IGraphicsCaptureSession2 nicht verfügbar — Cursor bleibt drin.
            }
            try {
                m_session.IsBorderRequired(false);
            } catch (const winrt::hresult_error&) {
                // IGraphicsCaptureSession3 nicht verfügbar — gelber Rahmen bleibt.
            }

            m_session.StartCapture();
        }

        ~CaptureWindowWinRT() override {
            m_session.Close();
            m_framePool.Close();
        }

        void setMinPullIntervalNs(int64_t ns) override { m_minPullIntervalNs = ns; }

        void setCursorCaptureEnabled(bool enabled) override {
            try {
                m_session.IsCursorCaptureEnabled(enabled);
            } catch (const winrt::hresult_error&) {
                // IGraphicsCaptureSession2 nicht verfügbar (vor Win10 2004) — no-op.
            }
        }

        bool setMinUpdateIntervalNs(int64_t ns) override {
            if (ns <= 0) return true; // 0 = aus (DWM-Default), nichts zu setzen
            try {
                // TimeSpan = 100-ns-Einheiten. Werte <1ms verhalten sich laut
                // Win32CaptureSample #82 buggy — wir kommen von Hz-Werten (<=60) → immer >=16ms.
                m_session.MinUpdateInterval(winrt::Windows::Foundation::TimeSpan{ns / 100});
                return true;
            } catch (const winrt::hresult_error&) {
                // OS ohne IGraphicsCaptureSession5 (vor 24H2/26100) — kein Fehler, nur kein C2.
                return false;
            }
        }

        ID3D11Texture2D* getSurface() override {
            // Window-Resize-Detection: GraphicsCaptureItem.Size aktualisiert sich live mit dem
            // Fenster. Wenn das Item größer/kleiner geworden ist, FramePool mit neuer Größe
            // recreate'n — sonst captured WGC weiter in der alten Auflösung (Cropping).
            // Läuft JEDEN Aufruf (auch wenn wir gleich den Pull drosseln) damit ein Resize
            // sofort erkannt wird.
            const auto currentSize = m_item.Size();
            if (currentSize.Width != m_lastSize.Width || currentSize.Height != m_lastSize.Height) {
                if (currentSize.Width > 0 && currentSize.Height > 0) {
                    m_framePool.Recreate(
                        m_interopDevice,
                        static_cast<winrt::Windows::Graphics::DirectX::DirectXPixelFormat>(DXGI_FORMAT_R8G8B8A8_UNORM),
                        2,
                        currentSize);
                    m_lastSize = currentSize;
                    m_lastCapturedFrame = nullptr;
                    m_lastCapturedSurface.Reset();
                }
            }

            // C (3.7.2026): WGC-Capture-Rate deckeln (gemessene Freeze-Ursache 3.7. =
            // WGC-Capture stallt in iRacings GPU-Leerlauf-Lücken bei leichter Last).
            // Der DWM füllt den FreeThreaded-FramePool auf SEINEM Takt; solange wir nicht
            // TryGetNextFrame rufen, staut sich der Pool (Backpressure) → DWM captured
            // seltener → weniger WGC-/GPU-Last. Zwischen den Pulls geben wir die zuletzt
            // gecapturte Surface zurück (der Frame wird gehalten → Pointer bleibt gültig).
            // Kein Throttle bis zur ersten Surface (Setup wartet sonst ewig) und wenn
            // m_minPullIntervalNs==0 (Alt-Verhalten). Analog OpenKneeboards „cap window
            // capture framerate". Nur die Bild-INHALT-Rate sinkt; die Quad-Pose läuft
            // Layer-seitig in voller Rate weiter.
            const auto now = std::chrono::steady_clock::now();
            const int64_t sinceNs = std::chrono::duration_cast<std::chrono::nanoseconds>(
                                        now - m_lastPull).count();
            const bool doPull = (m_minPullIntervalNs <= 0) || !m_lastCapturedSurface ||
                                (sinceNs >= m_minPullIntervalNs);
            if (doPull) {
                m_lastPull = now;
                auto frame = m_framePool.TryGetNextFrame();
                if (frame != nullptr) {
                    ComPtr<ID3D11Texture2D> surface;
                    auto access = frame.Surface().as<IDirect3DDXGIInterfaceAccess>();
                    CHECK_HRCMD(access->GetInterface(winrt::guid_of<ID3D11Texture2D>(),
                                                     reinterpret_cast<void**>(surface.ReleaseAndGetAddressOf())));

                    m_lastCapturedFrame = frame;
                    m_lastCapturedSurface = surface;
                }
            }

            return m_lastCapturedSurface.Get();
        }

      private:
        winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice m_interopDevice;
        winrt::Windows::Graphics::Capture::GraphicsCaptureItem m_item{nullptr};
        winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool m_framePool{nullptr};
        winrt::Windows::Graphics::Capture::GraphicsCaptureSession m_session{nullptr};
        winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame m_lastCapturedFrame{nullptr};
        ComPtr<ID3D11Texture2D> m_lastCapturedSurface;
        winrt::Windows::Graphics::SizeInt32 m_lastSize{};
        // C (3.7.2026): Capture-Rate-Throttle.
        int64_t m_minPullIntervalNs{0};
        std::chrono::steady_clock::time_point m_lastPull{};
    };

} // namespace openxr_api_layer::capture
