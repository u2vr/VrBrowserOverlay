using System;
using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using Valve.VR;

namespace VrBrowserTestCore
{
    internal sealed unsafe class D3D11OverlayRenderer : IRenderHandler
    {
        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        private const uint D3D11_SDK_VERSION = 7;

        private const uint DXGI_FORMAT_R8G8B8A8_UNORM = 28;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

        private const uint D3D11_USAGE_DEFAULT = 0;
        private const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
        private const uint D3D11_BIND_RENDER_TARGET = 0x20;

        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_SAMPLE_DESC
        {
            public uint Count;
            public uint Quality;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint ArraySize;
            public uint Format;
            public DXGI_SAMPLE_DESC SampleDesc;
            public uint Usage;
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_BOX
        {
            public uint left;
            public uint top;
            public uint front;
            public uint right;
            public uint bottom;
            public uint back;
        }

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            int driverType,
            IntPtr software,
            uint flags,
            IntPtr pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            out IntPtr ppDevice,
            out int pFeatureLevel,
            out IntPtr ppImmediateContext);

        private readonly object _syncLock = new();
        private IntPtr _device;
        private IntPtr _context;
        private IntPtr _overlayTexture;

        private int _width;
        private int _height;
        private int _colorFormat;
        private int _texWidth;
        private int _texHeight;
        private int _texColorFormat = -1;
        private ulong _overlayHandle;
        private volatile bool _disposed;

        public D3D11OverlayRenderer(ulong overlayHandle, int width, int height, int colorFormat)
        {
            _overlayHandle = overlayHandle;
            _width = width;
            _height = height;
            _colorFormat = colorFormat;

            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out _device,
                out _,
                out _context);

            if (hr < 0 || _device == IntPtr.Zero || _context == IntPtr.Zero)
            {
                throw new InvalidOperationException($"D3D11CreateDevice failed with HRESULT 0x{hr:X8}");
            }

            EnsureTexture(_width, _height, _colorFormat);
        }

        public void SetConfiguration(int width, int height, int colorFormat)
        {
            lock (_syncLock)
            {
                _width = width;
                _height = height;
                _colorFormat = colorFormat;
                if (!_disposed)
                {
                    EnsureTexture(_width, _height, _colorFormat);
                }
            }
        }

        private void EnsureTexture(int width, int height, int colorFormat)
        {
            if (_overlayTexture != IntPtr.Zero &&
                _texWidth == width &&
                _texHeight == height &&
                _texColorFormat == colorFormat)
            {
                return;
            }

            if (_overlayTexture != IntPtr.Zero)
            {
                ReleaseCom(_overlayTexture);
                _overlayTexture = IntPtr.Zero;
            }

            uint dxgiFormat = colorFormat == 1 ? DXGI_FORMAT_R8G8B8A8_UNORM : DXGI_FORMAT_B8G8R8A8_UNORM;

            D3D11_TEXTURE2D_DESC desc = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = dxgiFormat,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
                CPUAccessFlags = 0,
                MiscFlags = 0
            };

            IntPtr tex = IntPtr.Zero;
            // ID3D11Device::CreateTexture2D is vtable slot 5
            var createTex2D = (delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)(*(*(void***)_device + 5));
            int hr = createTex2D(_device, &desc, IntPtr.Zero, &tex);
            if (hr < 0 || tex == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateTexture2D failed with HRESULT 0x{hr:X8}");
            }

            _overlayTexture = tex;
            _texWidth = width;
            _texHeight = height;
            _texColorFormat = colorFormat;

            ClearTextureToTransparent();
        }

        public void ClearTextureToTransparent()
        {
            lock (_syncLock)
            {
                if (_disposed || _overlayTexture == IntPtr.Zero) return;

                IntPtr rtv = IntPtr.Zero;
                // ID3D11Device::CreateRenderTargetView is vtable slot 9
                var createRtv = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)(*(*(void***)_device + 9));
                int hr = createRtv(_device, _overlayTexture, IntPtr.Zero, &rtv);
                if (hr >= 0 && rtv != IntPtr.Zero)
                {
                    float* clearColor = stackalloc float[4] { 0f, 0f, 0f, 0f };
                    // ID3D11DeviceContext::ClearRenderTargetView is vtable slot 50
                    var clearRtv = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)(*(*(void***)_context + 50));
                    clearRtv(_context, rtv, clearColor);
                    FlushContext();
                    ReleaseCom(rtv);
                }

                SubmitToOpenVr();
            }
        }

        private void FlushContext()
        {
            // ID3D11DeviceContext::Flush is vtable slot 111
            var flush = (delegate* unmanaged[Stdcall]<IntPtr, void>)(*(*(void***)_context + 111));
            flush(_context);
        }

        private void SubmitToOpenVr()
        {
            if (_overlayHandle == 0 || _overlayTexture == IntPtr.Zero) return;

            Texture_t texture = new()
            {
                eColorSpace = EColorSpace.Auto,
                eType = ETextureType.DirectX,
                handle = _overlayTexture
            };
            OpenVR.Overlay?.SetOverlayTexture(_overlayHandle, ref texture);
        }

        private static void ReleaseCom(IntPtr comPtr)
        {
            if (comPtr != IntPtr.Zero)
            {
                // IUnknown::Release is vtable slot 2
                var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(*(void***)comPtr + 2));
                release(comPtr);
            }
        }

        #region CefSharp IRenderHandler Implementation

        public ScreenInfo? GetScreenInfo()
        {
            return new ScreenInfo { DeviceScaleFactor = 1.0f };
        }

        public Rect GetViewRect()
        {
            return new Rect(0, 0, _width, _height);
        }

        public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
        {
            screenX = viewX;
            screenY = viewY;
            return false;
        }

        public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
        {
            if (type != PaintElementType.View || acceleratedPaintInfo.SharedTextureHandle == IntPtr.Zero)
                return;

            lock (_syncLock)
            {
                if (_disposed || _device == IntPtr.Zero || _context == IntPtr.Zero)
                    return;

                EnsureTexture(_width, _height, _colorFormat);

                Guid iid = IID_ID3D11Texture2D;
                IntPtr sharedTex = IntPtr.Zero;
                // ID3D11Device::OpenSharedResource is vtable slot 28
                var openShared = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)_device + 28));
                int hr = openShared(_device, acceleratedPaintInfo.SharedTextureHandle, &iid, &sharedTex);
                if (hr >= 0 && sharedTex != IntPtr.Zero)
                {
                    try
                    {
                        int rx = Math.Clamp(dirtyRect.X, 0, _texWidth);
                        int ry = Math.Clamp(dirtyRect.Y, 0, _texHeight);
                        int rw = Math.Clamp(dirtyRect.Width, 0, _texWidth - rx);
                        int rh = Math.Clamp(dirtyRect.Height, 0, _texHeight - ry);

                        if (rw > 0 && rh > 0)
                        {
                            D3D11_BOX box = new()
                            {
                                left = (uint)rx,
                                top = (uint)ry,
                                front = 0,
                                right = (uint)(rx + rw),
                                bottom = (uint)(ry + rh),
                                back = 1
                            };

                            // ID3D11DeviceContext::CopySubresourceRegion is vtable slot 46
                            var copySubresource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)(*(*(void***)_context + 46));
                            copySubresource(_context, _overlayTexture, 0, (uint)rx, (uint)ry, 0, sharedTex, 0, &box);
                            FlushContext();
                            SubmitToOpenVr();
                        }
                    }
                    finally
                    {
                        ReleaseCom(sharedTex);
                    }
                }
            }
        }

        public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
        {
            if (type != PaintElementType.View || buffer == IntPtr.Zero || width <= 0 || height <= 0)
                return;

            lock (_syncLock)
            {
                if (_disposed || _device == IntPtr.Zero || _context == IntPtr.Zero)
                    return;

                bool textureRecreated = (_overlayTexture == IntPtr.Zero || _texWidth != width || _texHeight != height || _texColorFormat != _colorFormat);
                EnsureTexture(width, height, _colorFormat);

                int rx = textureRecreated ? 0 : Math.Clamp(dirtyRect.X, 0, width);
                int ry = textureRecreated ? 0 : Math.Clamp(dirtyRect.Y, 0, height);
                int rw = textureRecreated ? width : Math.Clamp(dirtyRect.Width, 0, width - rx);
                int rh = textureRecreated ? height : Math.Clamp(dirtyRect.Height, 0, height - ry);

                if (rw <= 0 || rh <= 0)
                    return;

                D3D11_BOX box = new()
                {
                    left = (uint)rx,
                    top = (uint)ry,
                    front = 0,
                    right = (uint)(rx + rw),
                    bottom = (uint)(ry + rh),
                    back = 1
                };

                uint rowPitch = (uint)(width * 4);
                byte* srcDirtyPtr = (byte*)buffer + ((long)ry * width + rx) * 4;

                // ID3D11DeviceContext::UpdateSubresource is vtable slot 48
                var updateSubresource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, D3D11_BOX*, void*, uint, uint, void>)(*(*(void***)_context + 48));
                updateSubresource(_context, _overlayTexture, 0, &box, srcDirtyPtr, rowPitch, 0);

                FlushContext();
                SubmitToOpenVr();
            }
        }

        public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo) { }

        public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) => false;

        public void UpdateDragCursor(DragOperationsMask operation) { }

        public void OnPopupShow(bool show) { }

        public void OnPopupSize(Rect rect) { }

        public void OnImeCompositionRangeChanged(CefSharp.Structs.Range selectedRange, Rect[] characterBounds) { }

        public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                _disposed = true;

                if (_overlayTexture != IntPtr.Zero)
                {
                    ReleaseCom(_overlayTexture);
                    _overlayTexture = IntPtr.Zero;
                }
                if (_context != IntPtr.Zero)
                {
                    ReleaseCom(_context);
                    _context = IntPtr.Zero;
                }
                if (_device != IntPtr.Zero)
                {
                    ReleaseCom(_device);
                    _device = IntPtr.Zero;
                }
            }
        }

        #endregion
    }
}
