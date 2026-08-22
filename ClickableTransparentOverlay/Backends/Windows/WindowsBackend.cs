using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using ClickableTransparentOverlay.Win32;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace ClickableTransparentOverlay.Backends.Windows;

/// <summary>
/// Backend implementation using Win32 and DirectX 11 with the goal of minimizing external dependencies and binary size.
/// </summary>
public class WindowsBackend : IBackend
{
    private const string ClassName = "MultiViewportImGuiFramework";
    
    private readonly ImGuiContextPtr _context;

    protected readonly ID3D11Device Device;
    protected readonly ID3D11DeviceContext DeviceContext;
    
    protected readonly IntPtr SelfPointer;
    protected readonly IntPtr Window;
    
    public static IBackend Create(ImGuiContextPtr ctx, string windowTitle) => new WindowsBackend(ctx, windowTitle);
    
    public void BeginRender()
    {
        if (User32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
        ImGuiImplWin32.NewFrame();
        ImGuiImplD3D11.NewFrame();
    }

    protected virtual unsafe void Construct(ImGuiContextPtr ctx, string windowTitle, out ID3D11Device device,
        out ID3D11DeviceContext deviceContext, out IntPtr selfPointer, out IntPtr window)
    {
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
#if DEBUG
            DeviceCreationFlags.Debug,
#else
            DeviceCreationFlags.None,
#endif
            [FeatureLevel.Level_10_0],
            out device,
            out deviceContext);
        if (result != Result.Ok)
        {
            throw new("Failed to create D3D11 device: " + result);
        }
        
        selfPointer = Kernel32.GetModuleHandle(null);
        var windowClass = new WNDCLASSEX
        {
            Size = Unsafe.SizeOf<WNDCLASSEX>(),
            WindowProc = WndProc,
            InstanceHandle = selfPointer,
            ClassName = ClassName,
        };

        if (User32.RegisterClassEx(ref windowClass) == 0)
        {
            throw new($"Failed to register window class with name {windowClass.ClassName}");
        }

        window = User32.CreateWindowEx(
            WindowExStyles.WS_EX_TOOLWINDOW,
            windowClass.ClassName,
            windowTitle,
            0, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            
        ImGuiImplWin32.SetCurrentContext(ctx);
        if (!ImGuiImplWin32.Init(window))
        {
            throw new("Failed to initialize Win32 backend for ImGui");
        }
        
        ImGuiImplD3D11.SetCurrentContext(ctx);
        if (!ImGuiImplD3D11.Init(new((Hexa.NET.ImGui.Backends.D3D11.ID3D11Device*)device.NativePointer),
                new((Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext*)deviceContext.NativePointer)))
        {
            throw new("Failed to initialize D3D11 backend for ImGui");
        }
    }

    public virtual void Dispose()
    {
        ImGuiImplD3D11.SetCurrentContext(_context);
        ImGuiImplD3D11.Shutdown();
        ImGuiImplD3D11.SetCurrentContext(ImGuiContextPtr.Null);

        ImGuiImplWin32.SetCurrentContext(_context);
        ImGuiImplWin32.Shutdown();
        ImGuiImplWin32.SetCurrentContext(ImGuiContextPtr.Null);

        User32.DestroyWindow(Window);
        User32.UnregisterClass(ClassName, SelfPointer);
        
        Device.Release();
        DeviceContext.Release();
        
        GC.SuppressFinalize(this);
    }
    
    protected virtual IntPtr HandleWindowEventInternal(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (ImGuiImplWin32.WndProcHandler(hWnd, msg, wParam, lParam) != 0)
        {
            return IntPtr.Zero;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }
    
    private IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
        => HandleWindowEventInternal(hWnd, msg, wParam, lParam);

    public unsafe ImTextureID LoadTexture<T>(Memory<T> memory, int width, int height, uint formatRaw)
    {
        var format = (Format)formatRaw;
        var texDesc = new Texture2DDescription(format, width, height, 1, 1);

        using MemoryHandle imageMemoryHandle = memory.Pin();
        var subResource = new SubresourceData(imageMemoryHandle.Pointer, texDesc.Width * 4);
        using var texture = Device.CreateTexture2D(texDesc, new[] { subResource });
        var resViewDesc = new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, format, 0, texDesc.MipLevels);
        return Device.CreateShaderResourceView(texture, resViewDesc).NativePointer;
    }

    public void FreeTexture(ImTextureID texture)
    {
        new ID3D11ShaderResourceView(texture).Release();
    }
    
    public void EndRender()
    {
        ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
    }

    public WindowsBackend(ImGuiContextPtr ctx, string windowTitle)
    {
        _context = ctx;
        Construct(ctx, windowTitle, out Device, out DeviceContext, out SelfPointer, out Window);
    }
}
