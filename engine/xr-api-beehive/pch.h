// MIT License
//
// << insert your own copyright here >>
//
// Based on https://github.com/mbucchia/OpenXR-Layer-Template.
// Copyright(c) 2022-2023 Matthieu Bucchianeri
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this softwareand associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and /or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright noticeand this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#pragma once

// Uncomment below the graphics frameworks used by the layer.

#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_D3D12

// Standard library.
#include <algorithm>
#include <cstdarg>
#include <ctime>
#define _USE_MATH_DEFINES
#include <cmath>
#include <deque>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <memory>
#include <optional>

using namespace std::chrono_literals;

// Windows header files.
#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers
#define NOMINMAX
#include <windows.h>
#include <unknwn.h>
#include <wrl.h>
#include <wil/resource.h>
#include <traceloggingactivity.h>
#include <traceloggingprovider.h>

using Microsoft::WRL::ComPtr;

// Graphics APIs.
#include <dxgiformat.h>
#ifdef XR_USE_GRAPHICS_API_D3D11
#include <d3d11_4.h>
#endif
#ifdef XR_USE_GRAPHICS_API_D3D12
#include <d3d12.h>
#endif
#include <d3dcompiler.h>

// Needed for window capture (Mbucchia overlay-desktop-window pattern).
#include <winrt/windows.foundation.h>
#include <winrt/windows.graphics.capture.h>
#include <windows.graphics.capture.interop.h>
#include <winrt/windows.graphics.directx.direct3d11.h>

// OpenXR + Windows-specific definitions.
#define XR_NO_PROTOTYPES
#define XR_USE_PLATFORM_WIN32
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

// OpenXR loader interfaces.
#include <loader_interfaces.h>

// OpenXR/DirectX utilities.
#include <XrError.h>
#include <XrMath.h>
#include <XrSide.h>
#include <XrStereoView.h>
#include <XrToString.h>
#include <DirectXCollision.h>

// Compat-Shim: MSVC 14.51 (VS 2026) hat stdext::checked_array_iterator entfernt.
// fmt 7.0.1 referenziert es im _SECURE_SCL-Pfad (Debug-Builds). Minimaler Back-Port
// damit fmt baut; kann weg sobald fmt auf >=8.x gehoben wird.
#if defined(_MSC_VER) && _MSC_VER >= 1951 && defined(_SECURE_SCL) && _SECURE_SCL
#include <iterator>
namespace stdext {
    // Minimaler Random-Access-Iterator-Wrapper über einen Pointer. Muss das Iterator-
    // Interface vollständig erfüllen, weil STL-Algorithmen (uninitialized_copy etc.)
    // ++ / * / += darauf aufrufen. Keine Bounds-Checks — wir wollen nur Kompatibilität.
    template <typename Iter>
    class checked_array_iterator {
      public:
        using iterator_category = std::random_access_iterator_tag;
        using value_type = typename std::iterator_traits<Iter>::value_type;
        using difference_type = typename std::iterator_traits<Iter>::difference_type;
        using pointer = typename std::iterator_traits<Iter>::pointer;
        using reference = typename std::iterator_traits<Iter>::reference;

        checked_array_iterator() = default;
        checked_array_iterator(Iter p, size_t) : m_ptr(p) {}

        reference operator*() const { return *m_ptr; }
        pointer operator->() const { return m_ptr; }
        reference operator[](difference_type n) const { return m_ptr[n]; }

        checked_array_iterator& operator++() { ++m_ptr; return *this; }
        checked_array_iterator operator++(int) { auto t = *this; ++m_ptr; return t; }
        checked_array_iterator& operator--() { --m_ptr; return *this; }
        checked_array_iterator operator--(int) { auto t = *this; --m_ptr; return t; }
        checked_array_iterator& operator+=(difference_type n) { m_ptr += n; return *this; }
        checked_array_iterator& operator-=(difference_type n) { m_ptr -= n; return *this; }

        friend checked_array_iterator operator+(checked_array_iterator a, difference_type n) { a += n; return a; }
        friend checked_array_iterator operator+(difference_type n, checked_array_iterator a) { a += n; return a; }
        friend checked_array_iterator operator-(checked_array_iterator a, difference_type n) { a -= n; return a; }
        friend difference_type operator-(checked_array_iterator a, checked_array_iterator b) { return a.m_ptr - b.m_ptr; }

        friend bool operator==(checked_array_iterator a, checked_array_iterator b) { return a.m_ptr == b.m_ptr; }
        friend bool operator!=(checked_array_iterator a, checked_array_iterator b) { return a.m_ptr != b.m_ptr; }
        friend bool operator<(checked_array_iterator a, checked_array_iterator b)  { return a.m_ptr <  b.m_ptr; }
        friend bool operator<=(checked_array_iterator a, checked_array_iterator b) { return a.m_ptr <= b.m_ptr; }
        friend bool operator>(checked_array_iterator a, checked_array_iterator b)  { return a.m_ptr >  b.m_ptr; }
        friend bool operator>=(checked_array_iterator a, checked_array_iterator b) { return a.m_ptr >= b.m_ptr; }

        Iter base() const { return m_ptr; }
        operator Iter() const { return m_ptr; }

      private:
        Iter m_ptr{};
    };
}
#endif

// FMT formatter.
#include <fmt/format.h>

#if defined(XR_USE_GRAPHICS_API_D3D11) || defined(XR_USE_GRAPHICS_API_D3D12)
// Utilities framework.
#include <utils/graphics.h>
#endif

#include <utils/inputs.h>
