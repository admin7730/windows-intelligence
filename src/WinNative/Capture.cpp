#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <cstring>
#include <mutex>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {
struct FrameSignal {
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Direct3D11CaptureFrame frame{nullptr};
    std::mutex mutex;
    ~FrameSignal() { if (event) CloseHandle(event); }
};

void Capture(HANDLE target, bool window, BYTE** pixels, int* width, int* height) {
    if (!GraphicsCaptureSession::IsSupported()) throw_hresult(E_NOTIMPL);
    auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{nullptr};
    const auto iid = guid_of<IGraphicsCaptureItem>();
    check_hresult(window ? factory->CreateForWindow(static_cast<HWND>(target), iid, put_abi(item))
                        : factory->CreateForMonitor(static_cast<HMONITOR>(target), iid, put_abi(item)));
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()));
    com_ptr<::IInspectable> inspectable;
    check_hresult(CreateDirect3D11DeviceFromDXGIDevice(device.as<IDXGIDevice>().get(), inspectable.put()));
    auto interopDevice = inspectable.as<IDirect3DDevice>();
    auto size = item.Size();
    if (size.Width < 1 || size.Height < 1) throw_hresult(E_INVALIDARG);
    auto pool = Direct3D11CaptureFramePool::CreateFreeThreaded(interopDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
    auto session = pool.CreateCaptureSession(item);
    session.IsCursorCaptureEnabled(false);
    auto signal = std::make_shared<FrameSignal>();
    if (!signal->event) throw_last_error();
    auto subscription = pool.FrameArrived(auto_revoke, [signal](auto const& sender, auto const&) noexcept {
        std::scoped_lock lock(signal->mutex);
        if (signal->frame) return;
        try { signal->frame = sender.TryGetNextFrame(); } catch (...) {}
        if (signal->frame) SetEvent(signal->event);
    });
    session.StartCapture();
    if (WaitForSingleObject(signal->event, 2200) != WAIT_OBJECT_0) throw_hresult(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
    subscription.revoke();
    Direct3D11CaptureFrame frame{nullptr};
    {
        std::scoped_lock lock(signal->mutex);
        frame = signal->frame;
    }
    auto content = frame.ContentSize();
    com_ptr<ID3D11Texture2D> source;
    check_hresult(frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>()->GetInterface(
        __uuidof(ID3D11Texture2D), source.put_void()));
    D3D11_TEXTURE2D_DESC desc{};
    source->GetDesc(&desc);
    int w = std::min(content.Width, static_cast<int>(desc.Width));
    int h = std::min(content.Height, static_cast<int>(desc.Height));
    if (w < 1 || h < 1) throw_hresult(E_FAIL);
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    com_ptr<ID3D11Texture2D> staging;
    check_hresult(device->CreateTexture2D(&desc, nullptr, staging.put()));
    context->CopyResource(staging.get(), source.get());
    const size_t stride = static_cast<size_t>(w) * 4;
    std::unique_ptr<BYTE, decltype(&CoTaskMemFree)> output(static_cast<BYTE*>(CoTaskMemAlloc(stride * h)), CoTaskMemFree);
    if (!output) throw_hresult(E_OUTOFMEMORY);
    D3D11_MAPPED_SUBRESOURCE data{};
    check_hresult(context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &data));
    for (int row = 0; row < h; ++row) {
        std::memcpy(output.get() + row * stride, static_cast<BYTE*>(data.pData) + row * data.RowPitch, stride);
    }
    context->Unmap(staging.get(), 0);
    session.Close();
    frame.Close();
    pool.Close();
    *width = w;
    *height = h;
    *pixels = output.release();
}
}

extern "C" __declspec(dllexport) HRESULT __stdcall CaptureOne(HANDLE target, BOOL window, BYTE** pixels, int* width, int* height) noexcept {
    *pixels = nullptr;
    *width = *height = 0;
    const auto apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(apartment) && apartment != RPC_E_CHANGED_MODE) return apartment;
    HRESULT result = S_OK;
    try { Capture(target, window != FALSE, pixels, width, height); }
    catch (...) { result = to_hresult(); }
    if (SUCCEEDED(apartment)) CoUninitialize();
    return result;
}
