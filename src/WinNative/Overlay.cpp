#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dcomp.h>
#include <wrl/client.h>
#include <memory>
#include <cstring>
#include <array>
#include <cmath>
#include "Backdrop.h"
#include "SceneVS.h"
#include "ScenePS.h"
#include "ParticleVS.h"
#include "ParticlePS.h"

using Microsoft::WRL::ComPtr;

namespace {
struct Scene {
    float width, height, time, scale;
    float orbX, orbY, radius, orbOpacity;
    float energy, mood, edgeOpacity, flash;
    float blur, glassOpacity, glow, palette;
};
static_assert(sizeof(Scene) == 64);

struct Regions {
    std::array<D3D11_RECT, 5> rect{};
    int count{};
    void Add(LONG l,LONG t,LONG r,LONG b) noexcept { if (l<r && t<b) rect[count++] = {l,t,r,b}; }
};

Regions Dirty(const Scene& scene) noexcept {
    auto w = static_cast<LONG>(scene.width), h = static_cast<LONG>(scene.height);
    Regions regions;
    if (scene.edgeOpacity + scene.flash >= .002f) {
        auto margin = static_cast<LONG>(std::ceil((17+12*scene.energy+28*scene.flash)*4.4f*scene.scale));
        auto x = (std::min)(margin,w/2), y = (std::min)(margin,h/2);
        regions.Add(0,0,w,y);
        regions.Add(0,h-y,w,h);
        regions.Add(0,y,x,h-y);
        regions.Add(w-x,y,w,h-y);
    }
    if (scene.orbOpacity >= .002f && scene.radius >= 1) {
        float r = scene.radius*1.35f + 2;
        // Screenshot particles can cross the center between the edge and the orb.
        if (scene.flash > .042f) { regions = {}; regions.Add(0,0,w,h); }
        else regions.Add((std::max)(0L,static_cast<LONG>(std::floor(scene.orbX-r))), (std::max)(0L,static_cast<LONG>(std::floor(scene.orbY-r))),
                         (std::min)(w,static_cast<LONG>(std::ceil(scene.orbX+r))), (std::min)(h,static_cast<LONG>(std::ceil(scene.orbY+r))));
    }
    return regions;
}

LRESULT CALLBACK OverlayWindow(HWND hwnd, UINT msg, WPARAM w, LPARAM l) {
    if (msg == WM_NCHITTEST) return HTTRANSPARENT;
    if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    return DefWindowProcW(hwnd, msg, w, l);
}

struct Overlay {
    HWND hwnd{};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11RenderTargetView> surface;
    ComPtr<ID3D11Texture2D> frameTexture;
    struct Staging { ComPtr<ID3D11Texture2D> pixels; Regions regions; Scene scene{}; };
    std::array<Staging, 2> staging;
    Regions previous;
    int head{}, pending{};
    unsigned long long transferred{};
    ComPtr<ID3D11VertexShader> vertex, particleVertex;
    ComPtr<ID3D11PixelShader> pixel, particlePixel;
    ComPtr<ID3D11BlendState> blend;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<ID3D11Buffer> constants;
    bool visible{};
    OrbBackdrop backdrop;
    HRESULT backdropStatus = E_PENDING;

    HDC memoryDc{};
    HBITMAP dib{};
    HGDIOBJ originalBitmap{};
    void* dibPixels{};
    POINT origin{};
    SIZE dimensions{};

    ~Overlay() {
        if (originalBitmap) SelectObject(memoryDc, originalBitmap);
        if (dib) DeleteObject(dib);
        if (memoryDc) DeleteDC(memoryDc);
        if (hwnd) DestroyWindow(hwnd);
    }

    HRESULT Init(int x, int y, int w, int h) {
        const auto module = GetModuleHandleW(nullptr);
        WNDCLASSW wc{};
        wc.lpfnWndProc = OverlayWindow;
        wc.hInstance = module;
        wc.lpszClassName = L"WindowsIntelligence.DirectCompositionOverlay";
        if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return HRESULT_FROM_WIN32(GetLastError());
        hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            wc.lpszClassName, L"Windows Intelligence · GPU overlay", WS_POPUP, x, y, w, h, nullptr, nullptr, module, nullptr);
        if (!hwnd) return HRESULT_FROM_WIN32(GetLastError());
        origin = {x,y};
        dimensions = {w,h};
        memoryDc = CreateCompatibleDC(nullptr);
        BITMAPINFO bitmap{};
        bitmap.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bitmap.bmiHeader.biWidth = w;
        bitmap.bmiHeader.biHeight = -h;
        bitmap.bmiHeader.biPlanes = 1;
        bitmap.bmiHeader.biBitCount = 32;
        dib = CreateDIBSection(memoryDc, &bitmap, DIB_RGB_COLORS, &dibPixels, nullptr, 0);
        if (!memoryDc || !dib) return HRESULT_FROM_WIN32(GetLastError());
        originalBitmap = SelectObject(memoryDc, dib);
        auto hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
        if (FAILED(hr)) return hr;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w;
        desc.Height = h;
        desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(hr = device->CreateTexture2D(&desc, nullptr, &frameTexture))) return hr;
        if (FAILED(hr = device->CreateRenderTargetView(frameTexture.Get(), nullptr, &surface))) return hr;
        desc.BindFlags = 0;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        for (auto& buffer : staging) {
            if (FAILED(hr = device->CreateTexture2D(&desc, nullptr, &buffer.pixels))) return hr;
        }
        if (FAILED(hr = device->CreateVertexShader(SceneVS, sizeof(SceneVS), nullptr, &vertex))) return hr;
        if (FAILED(hr = device->CreatePixelShader(ScenePS, sizeof(ScenePS), nullptr, &pixel))) return hr;
        if (FAILED(hr = device->CreateVertexShader(ParticleVSCode, sizeof(ParticleVSCode), nullptr, &particleVertex))) return hr;
        if (FAILED(hr = device->CreatePixelShader(ParticlePSCode, sizeof(ParticlePSCode), nullptr, &particlePixel))) return hr;
        D3D11_BUFFER_DESC buffer{};
        buffer.ByteWidth = sizeof(Scene);
        buffer.Usage = D3D11_USAGE_DEFAULT;
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        if (FAILED(hr = device->CreateBuffer(&buffer, nullptr, &constants))) return hr;
        D3D11_RASTERIZER_DESC raster{};
        raster.FillMode = D3D11_FILL_SOLID;
        raster.CullMode = D3D11_CULL_NONE;
        raster.DepthClipEnable = TRUE;
        raster.ScissorEnable = TRUE;
        if (FAILED(hr = device->CreateRasterizerState(&raster, &rasterizer))) return hr;

        D3D11_BLEND_DESC blendDesc{};
        auto& channel = blendDesc.RenderTarget[0];
        channel.BlendEnable = TRUE;
        channel.SrcBlend = channel.SrcBlendAlpha = D3D11_BLEND_ONE;
        channel.DestBlend = channel.DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        channel.BlendOp = channel.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        channel.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        hr = device->CreateBlendState(&blendDesc, &blend);
        if (SUCCEEDED(hr)) backdropStatus = backdrop.Init(hwnd, static_cast<float>(w), static_cast<float>(h));
        return hr;
    }

    HRESULT Draw(const Scene& scene, int count) {
        HRESULT presented = S_FALSE;
        if (pending) {
            auto& front = staging[head];
            D3D11_MAPPED_SUBRESOURCE source{};
            auto hr = context->Map(front.pixels.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &source);
            if (FAILED(hr) && hr != DXGI_ERROR_WAS_STILL_DRAWING) return hr;
            if (SUCCEEDED(hr)) {
                Blit(front, source);
                context->Unmap(front.pixels.Get(), 0);
                head = (head+1) % static_cast<int>(staging.size());
                --pending;
                presented = Present();
                if (FAILED(presented)) return presented;
            }
        }
        if (pending == static_cast<int>(staging.size())) return presented;
        auto& back = staging[(head+pending) % staging.size()];
        back.regions = Dirty(scene);
        if (back.regions.count == 0) return presented;
        back.scene = scene;
        context->UpdateSubresource(constants.Get(), 0, nullptr, &scene, 0, 0);
        D3D11_VIEWPORT viewport{0,0,scene.width,scene.height,0,1};
        context->RSSetViewports(1, &viewport);
        context->RSSetState(rasterizer.Get());
        context->OMSetRenderTargets(1, surface.GetAddressOf(), nullptr);
        context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        context->VSSetConstantBuffers(0, 1, constants.GetAddressOf());
        context->PSSetConstantBuffers(0, 1, constants.GetAddressOf());
        context->VSSetShader(vertex.Get(), nullptr, 0);
        context->PSSetShader(pixel.Get(), nullptr, 0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        for (int i=0; i<back.regions.count; ++i) {
            context->RSSetScissorRects(1, &back.regions.rect[i]);
            context->Draw(3, 0);
        }
        if (count > 0 && scene.orbOpacity > .002f) {
            D3D11_RECT full{0,0,dimensions.cx,dimensions.cy};
            context->RSSetScissorRects(1, &full);
            context->OMSetBlendState(blend.Get(), nullptr, 0xffffffff);
            context->VSSetShader(particleVertex.Get(), nullptr, 0);
            context->PSSetShader(particlePixel.Get(), nullptr, 0);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            context->DrawInstanced(4, static_cast<UINT>(count), 0, 0);
        }
        context->OMSetRenderTargets(0, nullptr, nullptr);
        for (int i=0; i<back.regions.count; ++i) {
            const auto& rect = back.regions.rect[i];
            D3D11_BOX area{static_cast<UINT>(rect.left),static_cast<UINT>(rect.top),0,
                           static_cast<UINT>(rect.right),static_cast<UINT>(rect.bottom),1};
            context->CopySubresourceRegion(back.pixels.Get(),0,area.left,area.top,0,frameTexture.Get(),0,&area);
        }
        ++pending;
        context->Flush();
        return presented;
    }

    void Blit(const Staging& frame,const D3D11_MAPPED_SUBRESOURCE& source) noexcept {
        const auto& scene=frame.scene;
        if (scene.radius >= 1 || previous.count) backdrop.Set(scene.orbX,scene.orbY,scene.radius*.985f,scene.orbOpacity*scene.blur);
        const size_t stride = static_cast<size_t>(dimensions.cx) * 4;
        auto dest=static_cast<BYTE*>(dibPixels);
        auto pixels=static_cast<const BYTE*>(source.pData);
        // Clear the previously visible regions before adding this frame's updated pixels.
        for (int i=0; i<previous.count; ++i) {
            const auto& rect=previous.rect[i];
            for (long row=rect.top; row<rect.bottom; ++row) {
                std::memset(dest+row*stride+rect.left*4,0,static_cast<size_t>(rect.right-rect.left)*4);
            }
        }
        for (int i=0; i<frame.regions.count; ++i) {
            const auto& rect=frame.regions.rect[i];
            size_t width=static_cast<size_t>(rect.right-rect.left)*4;
            for (long row=rect.top; row<rect.bottom; ++row) {
                std::memcpy(dest+row*stride+rect.left*4,pixels+row*source.RowPitch+rect.left*4,width);
            }
            transferred += width*static_cast<size_t>(rect.bottom-rect.top);
        }
        previous=frame.regions;
    }

    HRESULT Present() noexcept {
        POINT zero{};
        BLENDFUNCTION alpha{AC_SRC_OVER,0,255,AC_SRC_ALPHA};
        if (!UpdateLayeredWindow(hwnd, nullptr, &origin, &dimensions, memoryDc, &zero, 0, &alpha, ULW_ALPHA))
            return HRESULT_FROM_WIN32(GetLastError());
        return S_OK;
    }
};
}

extern "C" __declspec(dllexport) HRESULT __stdcall CreateOverlay(int x,int y,int w,int h,void** handle) noexcept {
    *handle = nullptr;
    auto instance = std::unique_ptr<Overlay>(new (std::nothrow) Overlay());
    if (!instance) return E_OUTOFMEMORY;
    const HRESULT result = instance->Init(x,y,w,h);
    if (SUCCEEDED(result)) *handle = instance.release();
    return result;
}

extern "C" __declspec(dllexport) void __stdcall ShowOverlay(void* handle, BOOL visible) noexcept {
    auto& instance = *static_cast<Overlay*>(handle);
    if (instance.visible == (visible != FALSE)) return;
    instance.visible = visible != FALSE;
    if (visible) SetWindowPos(instance.hwnd, HWND_TOPMOST, 0,0,0,0,SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW);
    else { ShowWindow(instance.hwnd, SW_HIDE); instance.pending = 0; instance.head = 0; }
}

extern "C" __declspec(dllexport) HRESULT __stdcall DrawOverlay(void* handle,const Scene* frame,int particles) noexcept {
    auto result = static_cast<Overlay*>(handle)->Draw(*frame,particles);
    return result == DXGI_ERROR_WAS_STILL_DRAWING ? S_FALSE : result;
}

extern "C" __declspec(dllexport) void __stdcall DestroyOverlay(void* handle) noexcept { delete static_cast<Overlay*>(handle); }

extern "C" __declspec(dllexport) HWND __stdcall OverlayHwnd(void* handle) noexcept { return static_cast<Overlay*>(handle)->hwnd; }

extern "C" __declspec(dllexport) HRESULT __stdcall OverlayBackdropStatus(void* handle) noexcept { return static_cast<Overlay*>(handle)->backdropStatus; }
extern "C" __declspec(dllexport) unsigned long long __stdcall OverlayTransferBytes(void* handle) noexcept { return static_cast<Overlay*>(handle)->transferred; }

extern "C" __declspec(dllexport) unsigned __stdcall OverlayPixel(void* handle,int x,int y) noexcept {
    auto& overlay = *static_cast<Overlay*>(handle);
    if (x < 0 || y < 0 || x >= overlay.dimensions.cx || y >= overlay.dimensions.cy) return 0;
    return static_cast<const unsigned*>(overlay.dibPixels)[static_cast<size_t>(y) * overlay.dimensions.cx + x];
}
