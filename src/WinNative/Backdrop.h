#pragma once

#include <dispatcherqueue.h>
#include <dwmapi.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <windows.ui.composition.interop.h>

class OrbBackdrop {
    using Queue = winrt::Windows::System::DispatcherQueue;
    using QueueController = winrt::Windows::System::DispatcherQueueController;
    using Compositor = winrt::Windows::UI::Composition::Compositor;
    using DesktopTarget = winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget;
    using Sprite = winrt::Windows::UI::Composition::SpriteVisual;
    using Ellipse = winrt::Windows::UI::Composition::CompositionEllipseGeometry;
    QueueController queueOwner{nullptr};
    Compositor compositor{nullptr};
    DesktopTarget target{nullptr};
    Sprite sprite{nullptr};
    Ellipse ellipse{nullptr};

public:
    HRESULT Init(HWND hwnd, float width, float height) noexcept {
        try {
            if (!Queue::GetForCurrentThread()) {
                DispatcherQueueOptions options{sizeof(options), DQTYPE_THREAD_CURRENT, DQTAT_COM_STA};
                winrt::check_hresult(CreateDispatcherQueueController(options,
                    reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(queueOwner))));
            }
            const BOOL enabled = TRUE;
            winrt::check_hresult(DwmSetWindowAttribute(hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, &enabled, sizeof(enabled)));
            compositor = Compositor();
            auto interop = compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
            winrt::check_hresult(interop->CreateDesktopWindowTarget(hwnd, FALSE,
                reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(winrt::put_abi(target))));
            sprite = compositor.CreateSpriteVisual();
            sprite.Size({width,height});
            sprite.IsHitTestVisible(false);
            sprite.Brush(compositor.CreateHostBackdropBrush());
            ellipse = compositor.CreateEllipseGeometry();
            sprite.Clip(compositor.CreateGeometricClip(ellipse));
            sprite.Opacity(0);
            target.Root(sprite);
            return S_OK;
        } catch (...) { return winrt::to_hresult(); }
    }

    void Set(float x,float y,float radius,float opacity) noexcept {
        if (!ellipse) return;
        try {
            ellipse.Center({x,y});
            ellipse.Radius({radius,radius});
            sprite.Opacity(opacity);
        } catch (...) { }
    }
};
