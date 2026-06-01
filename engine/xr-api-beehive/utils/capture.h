// Adapted verbatim from Mbucchia OpenXR-Layer-Template, branch examples/overlay-desktop-window.
// Copyright(c) 2022-2023 Matthieu Bucchianeri.
// Borrows code from StereoKit (Nick Klingensmith) and Win32CaptureSample (Robert Mikhayelyan).

#pragma once

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
            m_session.StartCapture();
        }

        ~CaptureWindowWinRT() override {
            m_session.Close();
            m_framePool.Close();
        }

        ID3D11Texture2D* getSurface() override {
            // Window-Resize-Detection: GraphicsCaptureItem.Size aktualisiert sich live mit dem
            // Fenster. Wenn das Item größer/kleiner geworden ist, FramePool mit neuer Größe
            // recreate'n — sonst captured WGC weiter in der alten Auflösung (Cropping).
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

            auto frame = m_framePool.TryGetNextFrame();
            if (frame != nullptr) {
                ComPtr<ID3D11Texture2D> surface;
                auto access = frame.Surface().as<IDirect3DDXGIInterfaceAccess>();
                CHECK_HRCMD(access->GetInterface(winrt::guid_of<ID3D11Texture2D>(),
                                                 reinterpret_cast<void**>(surface.ReleaseAndGetAddressOf())));

                m_lastCapturedFrame = frame;
                m_lastCapturedSurface = surface;
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
    };

} // namespace openxr_api_layer::capture
